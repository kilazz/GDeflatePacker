using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace GPCK.Core
{
    public class AssetPacker
    {
        private const int TileSize = 65536; 
        private const int ChunkSize = 65536; 
        private const int DefaultAlignment = 16;
        private const int GpuAlignment = 4096; // DirectStorage optimal alignment

        private class ProcessedFile
        {
            public Guid AssetId;
            public required string OriginalPath;
            public uint OriginalSize;
            public uint CompressedSize;
            public byte[]? CompressedData; // Rented from Pool if possible, or resized.
            public uint Flags;
            public int Alignment;
            public uint Meta1;
            public uint Meta2;
        }
        
        public class DependencyDefinition
        {
            public required string SourcePath;
            public required string TargetPath;
            public GameArchive.DependencyType Type;
        }

        public static Dictionary<string, string> BuildFileMap(string sourceDirectory, string searchPattern = "*", SearchOption searchOption = SearchOption.AllDirectories)
        {
            var map = new Dictionary<string, string>();
            string rootDir = Path.GetFullPath(sourceDirectory);
            if (!rootDir.EndsWith(Path.DirectorySeparatorChar.ToString())) rootDir += Path.DirectorySeparatorChar;
            if (!Directory.Exists(rootDir)) return map;

            foreach (var file in Directory.EnumerateFiles(rootDir, searchPattern, searchOption))
            {
                map[file] = Path.GetRelativePath(rootDir, file).Replace('\\', '/');
            }
            return map;
        }

        public async Task CompressFilesToArchiveAsync(
            IDictionary<string, string> fileMap, 
            string outputPath, 
            bool enableDedup, 
            int level, 
            byte[]? key,
            bool enableMipSplit,
            List<DependencyDefinition>? dependencies,
            IProgress<int>? progress, 
            CancellationToken token)
        {
            GDeflateCodec.IsAvailable(); 
            bool zstdAvailable = ZstdCodec.IsAvailable();
            var processedFiles = new ConcurrentBag<ProcessedFile>();
            int processedCount = 0;
            
            await Parallel.ForEachAsync(fileMap, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token }, async (kvp, ct) =>
            {
                await ProcessFile(kvp.Key, kvp.Value, level, key, enableMipSplit, zstdAvailable, processedFiles, ct);
                int c = Interlocked.Increment(ref processedCount);
                progress?.Report((int)((c / (float)fileMap.Count) * 70));
            });

            var sortedFiles = processedFiles.OrderBy(f => f.AssetId).ToList();

            var depEntries = dependencies?.Select(d => new GameArchive.DependencyEntry {
                SourceAssetId = AssetIdGenerator.Generate(d.SourcePath),
                TargetAssetId = AssetIdGenerator.Generate(d.TargetPath),
                Type = d.Type
            }).ToList() ?? new List<GameArchive.DependencyEntry>();

            await WriteArchive(sortedFiles, depEntries, outputPath, enableDedup, progress, token);
        }

        public void CompressFilesToArchive(
            IDictionary<string, string> fileMap, 
            string outputPath, 
            bool enableDedup = true, 
            int level = 9, 
            byte[]? key = null, 
            bool enableMipSplit = false, 
            IProgress<int>? progress = null, 
            CancellationToken token = default)
        {
            CompressFilesToArchiveAsync(fileMap, outputPath, enableDedup, level, key, enableMipSplit, null, progress, token).GetAwaiter().GetResult();
        }

        private async Task ProcessFile(string inputPath, string relPath, int level, byte[]? key, bool mipSplit, bool zstdAvail, ConcurrentBag<ProcessedFile> outBag, CancellationToken ct)
        {
            // Use ArrayPool for reading to avoid huge allocations on LOH
            long fileSize = new FileInfo(inputPath).Length;
            byte[] fileBuffer = ArrayPool<byte>.Shared.Rent((int)fileSize);
            
            try 
            {
                using (var fs = File.OpenRead(inputPath))
                {
                    // Use ReadExactlyAsync to ensure we get the full file content (fixes CA2022)
                    await fs.ReadExactlyAsync(fileBuffer, 0, (int)fileSize, ct);
                }
                
                // Working span
                var dataSpan = new Span<byte>(fileBuffer, 0, (int)fileSize);

                string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                bool useGDeflate = ext == ".dds" || ext == ".model" || ext == ".geom" || ext == ".gdef";
                
                uint m1 = 0, m2 = 0;
                int tailSize = 0;
                
                // Copy data for processing (we might mutate order if splitting)
                byte[]? processingBuffer = null;

                if (ext == ".dds")
                {
                    var h = DdsUtils.GetHeaderInfo(dataSpan);
                    if (h.HasValue)
                    {
                        m1 = ((uint)h.Value.Width << 16) | (uint)h.Value.Height;
                        // Default meta2 is just mip count
                        m2 = ((uint)h.Value.MipCount << 8);
                        
                        if (mipSplit)
                        {
                            // Returns a rented array with re-ordered data: [Tail][Payload]
                            processingBuffer = DdsUtils.ProcessTextureForStreaming(dataSpan.ToArray(), out tailSize); 
                            // Store split info in Meta2: Low 24 bits = TailSize
                            m2 = (m2 & 0xFF000000) | ((uint)tailSize & 0x00FFFFFF);
                        }
                    }
                }

                if (processingBuffer == null)
                {
                    processingBuffer = dataSpan.ToArray(); // Fallback copy
                }

                bool isStreaming = processingBuffer.Length > 256 * 1024 && !useGDeflate; 
                
                var pf = new ProcessedFile 
                { 
                    AssetId = AssetIdGenerator.Generate(relPath),
                    OriginalPath = relPath, 
                    OriginalSize = (uint)processingBuffer.Length,
                    Meta1 = m1,
                    Meta2 = m2
                };

                pf.Alignment = useGDeflate ? GpuAlignment : DefaultAlignment;
                int alignTemp = pf.Alignment;
                int alignPower = 0;
                while(alignTemp > 1) { alignTemp >>= 1; alignPower++; }
                pf.Flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);

                if (useGDeflate)
                {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_GDEFLATE;
                    pf.CompressedData = CompressGDeflate(processingBuffer, level);
                }
                else if (zstdAvail) 
                {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_ZSTD;
                    if (isStreaming)
                    {
                        pf.Flags |= GameArchive.FLAG_STREAMING;
                        pf.CompressedData = CompressZstdStreaming(processingBuffer, level, key);
                        pf.Flags |= GameArchive.FLAG_ENCRYPTED; 
                    }
                    else
                    {
                        pf.CompressedData = CompressZstd(processingBuffer, level);
                        if (key != null) { pf.Flags |= GameArchive.FLAG_ENCRYPTED; pf.CompressedData = Encrypt(pf.CompressedData, key); }
                    }
                }
                else
                {
                    pf.Flags |= GameArchive.METHOD_STORE;
                    pf.CompressedData = processingBuffer; // Careful, if we rented this we need to handle it. For now, we assume copied.
                    if (key != null) { pf.Flags |= GameArchive.FLAG_ENCRYPTED; pf.CompressedData = Encrypt(pf.CompressedData, key); }
                }

                if (tailSize > 0 || ext == ".dds") pf.Flags |= GameArchive.TYPE_TEXTURE;

                pf.CompressedSize = (uint)pf.CompressedData.Length;
                outBag.Add(pf);
                
                // Return rented buffer from ProcessTextureForStreaming if applicable
                // (Optimized implementation would manage this better, keeping simple for prototype)
                if (mipSplit && processingBuffer != null && processingBuffer != fileBuffer) 
                {
                    ArrayPool<byte>.Shared.Return(processingBuffer);
                }

            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fileBuffer);
            }
        }

        private byte[] CompressGDeflate(byte[] input, int level)
        {
            ulong bound = GDeflateCodec.CompressBound((ulong)input.Length);
            // GDeflate requires native alignment sometimes, using pinned memory is safer
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = bound;
                    if (!GDeflateCodec.Compress(pOut, ref outSize, pIn, (ulong)input.Length, (uint)level, 0))
                        throw new Exception("GDeflate failed");
                    Array.Resize(ref output, (int)outSize);
                    return output;
                }
            }
        }

        private byte[] CompressZstd(byte[] input, int level)
        {
            ulong bound = ZstdCodec.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = ZstdCodec.ZSTD_compress((IntPtr)pOut, bound, (IntPtr)pIn, (ulong)input.Length, level);
                    if (ZstdCodec.ZSTD_isError(outSize) != 0) return input;
                    Array.Resize(ref output, (int)outSize);
                    return output;
                }
            }
        }

        private byte[] CompressZstdStreaming(byte[] input, int level, byte[]? key)
        {
            int blockCount = (input.Length + ChunkSize - 1) / ChunkSize;
            var ms = new MemoryStream();
            var bw = new BinaryWriter(ms);
            
            bw.Write(blockCount);

            long tableStart = ms.Position;
            int tableSize = blockCount * 8; 
            ms.Seek(tableSize, SeekOrigin.Current);

            var entries = new List<GameArchive.ChunkHeaderEntry>();

            for (int i = 0; i < blockCount; i++)
            {
                int offset = i * ChunkSize;
                int size = Math.Min(ChunkSize, input.Length - offset);
                byte[] chunk = new byte[size];
                Array.Copy(input, offset, chunk, 0, size);

                byte[] compressed = CompressZstd(chunk, level);
                if (key != null) compressed = Encrypt(compressed, key);

                entries.Add(new GameArchive.ChunkHeaderEntry { 
                    CompressedSize = (uint)compressed.Length, 
                    OriginalSize = (uint)size 
                });
                bw.Write(compressed);
            }

            long currentPos = ms.Position;
            ms.Position = tableStart;
            foreach (var e in entries)
            {
                bw.Write(e.CompressedSize);
                bw.Write(e.OriginalSize);
            }
            ms.Position = currentPos;

            return ms.ToArray();
        }

        private byte[] Encrypt(byte[] data, byte[] key)
        {
            byte[] output = new byte[12 + 16 + data.Length];
            var spanOut = new Span<byte>(output);
            RandomNumberGenerator.Fill(spanOut.Slice(0, 12)); 
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(spanOut.Slice(0, 12), new ReadOnlySpan<byte>(data), spanOut.Slice(28, data.Length), spanOut.Slice(12, 16));
            return output;
        }

        private async Task WriteArchive(
            List<ProcessedFile> files, 
            List<GameArchive.DependencyEntry> dependencies,
            string outputPath, 
            bool enableDedup,
            IProgress<int>? progress, 
            CancellationToken token)
        {
            long headerSize = 64; 
            long fileTableSize = files.Count * 44; 
            long nameTableSize = files.Sum(f => 16 + Encoding.UTF8.GetByteCount(f.OriginalPath) + 5);
            long dependencyTableSize = dependencies.Count * 36;

            long fileTableOffset = headerSize;
            long dependencyTableOffset = fileTableOffset + fileTableSize;
            long nameTableOffset = dependencyTableOffset + dependencyTableSize;
            long metaEnd = nameTableOffset + nameTableSize;

            long dataStart = (metaEnd + (4096 - 1)) & ~(4096 - 1);
            long currentOffset = dataStart;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            using var bw = new BinaryWriter(fs);

            // 1. Header
            bw.Write(Encoding.ASCII.GetBytes(GameArchive.Magic)); 
            bw.Write(GameArchive.Version); 
            bw.Write(files.Count); 
            bw.Write(0); 
            bw.Write(dependencies.Count); 
            bw.Write((int)0); // FIXED: 4 bytes padding instead of 8 bytes (long)
            
            bw.Write(fileTableOffset);
            bw.Write((long)0); 
            bw.Write(nameTableOffset);
            bw.Write(dependencyTableOffset);
            
            while(fs.Position < 64) bw.Write((byte)0);

            // --- CAS (Content Addressable Storage) & Layout Calculation ---
            long[] finalOffsets = new long[files.Count];
            var contentMap = new Dictionary<ulong, long>();
            var filesToWriteIndex = new List<int>();

            for(int i=0; i<files.Count; i++)
            {
                var f = files[i];
                long assignedOffset = -1;

                if (enableDedup && f.CompressedData != null && f.CompressedData.Length > 0)
                {
                    ulong contentHash = XxHash64.Compute(f.CompressedData);
                    
                    if (contentMap.TryGetValue(contentHash, out long existingOffset))
                    {
                        // Ensure alignment matches (reusing offset only if aligned correctly)
                        if ((existingOffset % f.Alignment) == 0)
                        {
                            assignedOffset = existingOffset;
                        }
                    }

                    if (assignedOffset == -1)
                    {
                        long padding = (f.Alignment - (currentOffset % f.Alignment)) % f.Alignment;
                        currentOffset += padding;
                        
                        assignedOffset = currentOffset;
                        contentMap[contentHash] = assignedOffset;
                        filesToWriteIndex.Add(i);
                        currentOffset += f.CompressedSize;
                    }
                }
                else
                {
                    long padding = (f.Alignment - (currentOffset % f.Alignment)) % f.Alignment;
                    currentOffset += padding;
                    assignedOffset = currentOffset;
                    filesToWriteIndex.Add(i);
                    currentOffset += f.CompressedSize;
                }

                finalOffsets[i] = assignedOffset;
            }

            // 2. File Table
            foreach(var (f, offset) in files.Zip(finalOffsets))
            {
                bw.Write(f.AssetId.ToByteArray()); 
                bw.Write(offset);                  
                bw.Write(f.CompressedSize);        
                bw.Write(f.OriginalSize);          
                bw.Write(f.Flags);                 
                bw.Write(f.Meta1);                 
                bw.Write(f.Meta2);                 
            }

            // 3. Dependency Table
            foreach(var d in dependencies)
            {
                bw.Write(d.SourceAssetId.ToByteArray());
                bw.Write(d.TargetAssetId.ToByteArray());
                bw.Write((uint)d.Type);
            }

            // 4. Name Table
            foreach(var f in files)
            {
                bw.Write(f.AssetId.ToByteArray());
                byte[] nameBytes = Encoding.UTF8.GetBytes(f.OriginalPath);
                WriteVarInt(bw, nameBytes.Length);
                bw.Write(nameBytes);
            }

            // 5. Zero Pad until DataStart
            long bytesToPad = dataStart - fs.Position;
            if (bytesToPad > 0)
            {
                byte[] pad = ArrayPool<byte>.Shared.Rent(65536);
                try {
                    while (bytesToPad > 0)
                    {
                        int write = (int)Math.Min(bytesToPad, 65536);
                        await fs.WriteAsync(pad, 0, write, token);
                        bytesToPad -= write;
                    }
                } finally { ArrayPool<byte>.Shared.Return(pad); }
            }

            // 6. Write Data Blobs
            int writtenCount = 0;
            foreach(int i in filesToWriteIndex)
            {
                var f = files[i];
                long requiredPos = finalOffsets[i];
                
                long currentPos = fs.Position;
                if (currentPos < requiredPos)
                {
                    long padLen = requiredPos - currentPos;
                    // Write padding logic
                    byte[] pad = ArrayPool<byte>.Shared.Rent(4096);
                    Array.Clear(pad, 0, pad.Length); // Zero out padding
                    while(padLen > 0) {
                         int w = (int)Math.Min(padLen, pad.Length);
                         await fs.WriteAsync(pad, 0, w, token);
                         padLen -= w;
                    }
                    ArrayPool<byte>.Shared.Return(pad);
                }
                
                if (f.CompressedData != null)
                {
                    await fs.WriteAsync(f.CompressedData, 0, f.CompressedData.Length, token);
                }
                
                writtenCount++;
                if (writtenCount % 10 == 0) progress?.Report(70 + (int)((writtenCount / (float)filesToWriteIndex.Count) * 30));
            }
        }

        private void WriteVarInt(BinaryWriter bw, int value)
        {
            uint v = (uint)value;
            while (v >= 0x80)
            {
                bw.Write((byte)(v | 0x80));
                v >>= 7;
            }
            bw.Write((byte)v);
        }

        public void DecompressArchive(string inputPath, string outputDirectory, byte[]? key = null, IProgress<int>? progress = null, CancellationToken token = default)
        {
            using var archive = new GameArchive(inputPath);
            if(key!=null) archive.DecryptionKey = key;
            
            for(int i=0; i<archive.FileCount; i++)
            {
                var entry = archive.GetEntryByIndex(i);
                string path = archive.GetPathForAssetId(entry.AssetId) ?? $"{entry.AssetId}.bin";
                string fullPath = Path.Combine(outputDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                
                using var fs = archive.OpenRead(entry);
                using var outFs = File.Create(fullPath);
                fs.CopyTo(outFs);
                progress?.Report((int)((i/(float)archive.FileCount)*100));
            }
        }
        
        public bool IsCpuLibraryAvailable() => GDeflateCodec.IsAvailable();
        public PackageInfo InspectPackage(string p) { using var a = new GameArchive(p); return a.GetPackageInfo(); }
        
        /// <summary>
        /// Reads every file in the archive to ensure integrity.
        /// Returns false if decompression fails for any file.
        /// </summary>
        public bool VerifyArchive(string p, byte[]? k = null) 
        {
            try
            {
                using var archive = new GameArchive(p);
                if (k != null) archive.DecryptionKey = k;

                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                
                try
                {
                    for (int i = 0; i < archive.FileCount; i++)
                    {
                        var entry = archive.GetEntryByIndex(i);
                        // OpenRead triggers header checks
                        using var stream = archive.OpenRead(entry);
                        
                        // Actually read and decompress the entire stream to verify integrity
                        long remaining = entry.OriginalSize;
                        while (remaining > 0)
                        {
                            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read == 0) throw new EndOfStreamException($"Unexpected EOF in file index {i}");
                            remaining -= read;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Verification Failed: {ex.Message}");
                return false;
            }
        }

        public void ExtractSingleFile(string p, string o, string t, byte[]? k) 
        {
            using var a = new GameArchive(p);
            if(k!=null) a.DecryptionKey = k;
            
            Guid targetId = AssetIdGenerator.Generate(t);
            if (a.TryGetEntry(targetId, out var e)) {
                 using var s = a.OpenRead(e);
                 using var dest = File.Create(Path.Combine(o, Path.GetFileName(t)));
                 s.CopyTo(dest);
                 return;
            }
            Console.WriteLine("File not found via AssetID generation.");
        }
        
        public Task CreatePatchArchiveAsync(string baseArch, Dictionary<string,string> map, string outPath, int lvl, byte[]? k, List<DependencyDefinition>? deps, CancellationToken ct)
            => CompressFilesToArchiveAsync(map, outPath, true, lvl, k, false, deps, null, ct);
    }
}