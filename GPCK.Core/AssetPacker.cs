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
        private const int ChunkSize = 65536; 
        private const int DefaultAlignment = 16;
        private const int GpuAlignment = 4096;
        private const long LargeFileThreshold = 250 * 1024 * 1024; // 250MB threshold for streaming

        public enum CompressionMethod
        {
            Auto,
            Store,
            GDeflate,
            Zstd,
            LZ4
        }

        private class ProcessedFile
        {
            public Guid AssetId;
            public required string OriginalPath;
            public uint OriginalSize;
            public uint CompressedSize;
            public byte[]? CompressedData; // Null if using StreamingData
            public byte[]? StreamingData;  // Contains [ChunkTable][CompChunk1][CompChunk2]...
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

        public static Dictionary<string, string> BuildFileMap(string sourceDirectory)
        {
            var map = new Dictionary<string, string>();
            string rootDir = Path.GetFullPath(sourceDirectory);
            if (!rootDir.EndsWith(Path.DirectorySeparatorChar.ToString())) rootDir += Path.DirectorySeparatorChar;
            if (!Directory.Exists(rootDir)) return map;

            foreach (var file in Directory.EnumerateFiles(rootDir, "*", SearchOption.AllDirectories))
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
            CancellationToken token,
            CompressionMethod forceMethod = CompressionMethod.Auto)
        {
            CodecGDeflate.IsAvailable(); 
            bool zstdAvailable = CodecZstd.IsAvailable();
            bool lz4Available = CodecLZ4.IsAvailable();

            var processedFiles = new ConcurrentBag<ProcessedFile>();
            int processedCount = 0;
            
            await Parallel.ForEachAsync(fileMap, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token }, async (kvp, ct) =>
            {
                await ProcessFile(kvp.Key, kvp.Value, level, key, enableMipSplit, zstdAvailable, lz4Available, forceMethod, processedFiles, ct);
                int c = Interlocked.Increment(ref processedCount);
                progress?.Report((int)((c / (float)fileMap.Count) * 80));
            });

            var sortedFiles = processedFiles.OrderBy(f => f.AssetId).ToList();

            var depEntries = dependencies?.Select(d => new GameArchive.DependencyEntry {
                SourceAssetId = AssetIdGenerator.Generate(d.SourcePath),
                TargetAssetId = AssetIdGenerator.Generate(d.TargetPath),
                Type = d.Type
            }).ToList() ?? new List<GameArchive.DependencyEntry>();

            await WriteArchive(sortedFiles, depEntries, outputPath, enableDedup, progress, token);
        }

        private async Task ProcessFile(
            string inputPath, 
            string relPath, 
            int level, 
            byte[]? key, 
            bool mipSplit, 
            bool zstdAvailable, 
            bool lz4Available,
            CompressionMethod forceMethod,
            ConcurrentBag<ProcessedFile> outBag, 
            CancellationToken ct)
        {
            var info = new FileInfo(inputPath);
            long fileSize = info.Length;
            
            // Check for large files to use streaming mode
            bool isLargeFile = fileSize >= LargeFileThreshold;
            
            // Streaming currently only supports ZSTD logic in this impl
            if (isLargeFile && !zstdAvailable)
            {
                 if (fileSize > int.MaxValue - 1024) throw new NotSupportedException($"File too large and Zstd not available: {relPath}");
            }

            if (isLargeFile && zstdAvailable && (forceMethod == CompressionMethod.Auto || forceMethod == CompressionMethod.Zstd))
            {
                // STREAMING PATH (Chunked Compression)
                using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                byte[] streamedBlob = await CompressZstdStreamingAsync(fs, fileSize, level, key, ct);
                
                var pf = new ProcessedFile 
                { 
                    AssetId = AssetIdGenerator.Generate(relPath),
                    OriginalPath = relPath, 
                    OriginalSize = (uint)fileSize,
                    StreamingData = streamedBlob, 
                    Alignment = DefaultAlignment,
                    Flags = GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_ZSTD | GameArchive.FLAG_STREAMING
                };
                
                int alignTemp = pf.Alignment;
                int alignPower = 0;
                while(alignTemp > 1) { alignTemp >>= 1; alignPower++; }
                pf.Flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);
                
                if (key != null) pf.Flags |= GameArchive.FLAG_ENCRYPTED;
                
                pf.CompressedSize = (uint)pf.StreamingData.Length;
                outBag.Add(pf);
                return;
            }

            // STANDARD PATH
            byte[] fileBuffer = ArrayPool<byte>.Shared.Rent((int)fileSize);
            
            try 
            {
                using (var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous))
                {
                    await fs.ReadExactlyAsync(fileBuffer, 0, (int)fileSize, ct);
                }
                
                var dataSpan = new Span<byte>(fileBuffer, 0, (int)fileSize);
                string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                
                // Determine Method
                bool useGDeflate = false;
                bool useZstd = false;
                bool useLz4 = false;

                if (forceMethod == CompressionMethod.Auto)
                {
                    useGDeflate = ext == ".dds" || ext == ".model" || ext == ".geom";
                    if (!useGDeflate) useZstd = zstdAvailable;
                }
                else
                {
                    switch(forceMethod)
                    {
                        case CompressionMethod.GDeflate: useGDeflate = true; break;
                        case CompressionMethod.Zstd: useZstd = true; break;
                        case CompressionMethod.LZ4: useLz4 = true; break;
                        case CompressionMethod.Store: break;
                    }
                }

                // Fallback checks
                if (useZstd && !zstdAvailable) { useZstd = false; }
                if (useLz4 && !lz4Available) { useLz4 = false; } // Fallback to store
                
                uint m1 = 0, m2 = 0;
                int tailSize = 0;
                byte[]? processingBuffer = null;

                if (ext == ".dds")
                {
                    var h = DdsUtils.GetHeaderInfo(dataSpan);
                    if (h.HasValue)
                    {
                        m1 = ((uint)h.Value.Width << 16) | (uint)h.Value.Height;
                        m2 = ((uint)h.Value.MipCount << 8);
                        
                        if (mipSplit)
                        {
                            processingBuffer = DdsUtils.ProcessTextureForStreaming(dataSpan.ToArray(), out tailSize); 
                            m2 = (m2 & 0xFF000000) | ((uint)tailSize & 0x00FFFFFF);
                        }
                    }
                }

                if (processingBuffer == null) processingBuffer = dataSpan.ToArray();

                var pf = new ProcessedFile 
                { 
                    AssetId = AssetIdGenerator.Generate(relPath),
                    OriginalPath = relPath, 
                    OriginalSize = (uint)processingBuffer.Length,
                    Meta1 = m1,
                    Meta2 = m2,
                    Alignment = useGDeflate ? GpuAlignment : DefaultAlignment
                };

                int alignTemp = pf.Alignment;
                int alignPower = 0;
                while(alignTemp > 1) { alignTemp >>= 1; alignPower++; }
                pf.Flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);

                if (useGDeflate)
                {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_GDEFLATE;
                    pf.CompressedData = CompressGDeflate(processingBuffer, level);
                }
                else if (useZstd) 
                {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_ZSTD;
                    pf.CompressedData = CompressZstd(processingBuffer, level);
                }
                else if (useLz4)
                {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_LZ4;
                    pf.CompressedData = CompressLZ4(processingBuffer, level);
                }
                else
                {
                    pf.Flags |= GameArchive.METHOD_STORE;
                    pf.CompressedData = processingBuffer; 
                }

                // Encryption applies to result
                if (key != null) { pf.Flags |= GameArchive.FLAG_ENCRYPTED; pf.CompressedData = Encrypt(pf.CompressedData, key); }

                if (tailSize > 0 || ext == ".dds") pf.Flags |= GameArchive.TYPE_TEXTURE;
                pf.CompressedSize = (uint)pf.CompressedData.Length;
                outBag.Add(pf);

                if (mipSplit && processingBuffer != null && processingBuffer != fileBuffer) ArrayPool<byte>.Shared.Return(processingBuffer);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fileBuffer);
            }
        }

        // --- Compression Helpers ---

        private byte[] CompressGDeflate(byte[] input, int level)
        {
            ulong bound = CodecGDeflate.CompressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = bound;
                    if (!CodecGDeflate.Compress(pOut, ref outSize, pIn, (ulong)input.Length, (uint)level, 0)) throw new Exception("GDeflate failed");
                    Array.Resize(ref output, (int)outSize);
                    return output;
                }
            }
        }

        private byte[] CompressZstd(byte[] input, int level)
        {
            ulong bound = CodecZstd.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = CodecZstd.ZSTD_compress((IntPtr)pOut, bound, (IntPtr)pIn, (ulong)input.Length, level);
                    if (CodecZstd.ZSTD_isError(outSize) != 0) return input;
                    Array.Resize(ref output, (int)outSize);
                    return output;
                }
            }
        }

        private byte[] CompressLZ4(byte[] input, int level)
        {
            int bound = CodecLZ4.LZ4_compressBound(input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    int outSize;
                    if (level < 3)
                        outSize = CodecLZ4.LZ4_compress_default((IntPtr)pIn, (IntPtr)pOut, input.Length, bound);
                    else 
                        outSize = CodecLZ4.LZ4_compress_HC((IntPtr)pIn, (IntPtr)pOut, input.Length, bound, level); // HC uses level 3-12

                    if (outSize <= 0) return input; // Fail or larger
                    Array.Resize(ref output, outSize);
                    return output;
                }
            }
        }
        
        private async Task<byte[]> CompressZstdStreamingAsync(Stream inputStream, long totalSize, int level, byte[]? key, CancellationToken ct)
        {
            // Calculate chunks
            int blockCount = (int)((totalSize + ChunkSize - 1) / ChunkSize);
            
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            
            bw.Write(blockCount);

            long tableStart = ms.Position;
            int tableSize = blockCount * 8; // 4 bytes size + 4 bytes orig
            ms.Seek(tableSize, SeekOrigin.Current); // Reserve space for table

            var entries = new List<GameArchive.ChunkHeaderEntry>(blockCount);
            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);

            try
            {
                for (int i = 0; i < blockCount; i++)
                {
                    int read = await inputStream.ReadAsync(chunkBuffer, 0, ChunkSize, ct);
                    if (read == 0) break;
                    
                    byte[] chunkData;
                    if (read < ChunkSize) {
                        chunkData = new byte[read];
                        Array.Copy(chunkBuffer, chunkData, read);
                    } else {
                         chunkData = chunkBuffer; 
                    }
                    
                    byte[] compressed;
                    if (read == ChunkSize) compressed = CompressZstd(chunkBuffer, level);
                    else compressed = CompressZstd(chunkData, level);

                    if (key != null) compressed = Encrypt(compressed, key);

                    entries.Add(new GameArchive.ChunkHeaderEntry { 
                        CompressedSize = (uint)compressed.Length, 
                        OriginalSize = (uint)read 
                    });
                    
                    bw.Write(compressed);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuffer);
            }

            // Write Table
            long endPos = ms.Position;
            ms.Position = tableStart;
            foreach (var e in entries)
            {
                bw.Write(e.CompressedSize);
                bw.Write(e.OriginalSize);
            }
            ms.Position = endPos;
            
            return ms.ToArray();
        }

        private byte[] Encrypt(byte[] data, byte[] key)
        {
            byte[] output = new byte[28 + data.Length];
            var spanOut = new Span<byte>(output);
            RandomNumberGenerator.Fill(spanOut.Slice(0, 12)); 
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(spanOut.Slice(0, 12), new ReadOnlySpan<byte>(data), spanOut.Slice(28, data.Length), spanOut.Slice(12, 16));
            return output;
        }

        public void CompressFilesToArchive(
            IDictionary<string, string> fileMap, 
            string outputPath, 
            bool enableDedup = true, 
            int level = 9, 
            byte[]? key = null, 
            bool enableMipSplit = false, 
            IProgress<int>? progress = null, 
            CancellationToken token = default,
            CompressionMethod method = CompressionMethod.Auto)
        {
            CompressFilesToArchiveAsync(fileMap, outputPath, enableDedup, level, key, enableMipSplit, null, progress, token, method).GetAwaiter().GetResult();
        }

        private async Task WriteArchive(List<ProcessedFile> files, List<GameArchive.DependencyEntry> dependencies, string outputPath, bool enableDedup, IProgress<int>? progress, CancellationToken token)
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

            // Correct Header Structure
            bw.Write(Encoding.ASCII.GetBytes(GameArchive.MagicStr)); 
            bw.Write(GameArchive.Version); 
            bw.Write(files.Count); 
            bw.Write(0); // Pad1
            bw.Write(dependencies.Count); 
            bw.Write((int)0); // Pad2
            bw.Write(fileTableOffset);
            bw.Write((long)0); // Reserved
            bw.Write(nameTableOffset);
            bw.Write(dependencyTableOffset);
            while(fs.Position < 64) bw.Write((byte)0);

            long[] finalOffsets = new long[files.Count];
            var contentMap = new Dictionary<ulong, long>();
            var filesToWriteIndex = new List<int>();

            for(int i=0; i<files.Count; i++)
            {
                var f = files[i];
                long assignedOffset = -1;
                
                // Determine source data (either regular compressed or streaming blob)
                byte[]? sourceData = f.CompressedData ?? f.StreamingData;

                if (enableDedup && sourceData != null)
                {
                    ulong contentHash = XxHash64.Compute(sourceData);
                    if (contentMap.TryGetValue(contentHash, out long existingOffset) && (existingOffset % f.Alignment) == 0)
                    {
                        assignedOffset = existingOffset;
                    }
                    else
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

            foreach(var d in dependencies)
            {
                bw.Write(d.SourceAssetId.ToByteArray());
                bw.Write(d.TargetAssetId.ToByteArray());
                bw.Write((uint)d.Type);
            }

            foreach(var f in files)
            {
                bw.Write(f.AssetId.ToByteArray());
                byte[] nameBytes = Encoding.UTF8.GetBytes(f.OriginalPath);
                WriteVarInt(bw, nameBytes.Length);
                bw.Write(nameBytes);
            }

            long bytesToPad = dataStart - fs.Position;
            if (bytesToPad > 0)
            {
                byte[] pad = ArrayPool<byte>.Shared.Rent(65536);
                while (bytesToPad > 0)
                {
                    int write = (int)Math.Min(bytesToPad, 65536);
                    await fs.WriteAsync(pad, 0, write, token);
                    bytesToPad -= write;
                }
                ArrayPool<byte>.Shared.Return(pad);
            }

            int writtenCount = 0;
            foreach(int i in filesToWriteIndex)
            {
                var f = files[i];
                long padLen = finalOffsets[i] - fs.Position;
                if(padLen > 0)
                {
                    byte[] pad = ArrayPool<byte>.Shared.Rent(4096);
                    Array.Clear(pad, 0, pad.Length);
                    while(padLen > 0) { int w=(int)Math.Min(padLen, pad.Length); await fs.WriteAsync(pad,0,w,token); padLen-=w; }
                    ArrayPool<byte>.Shared.Return(pad);
                }
                
                byte[]? sourceData = f.CompressedData ?? f.StreamingData;
                if (sourceData != null) await fs.WriteAsync(sourceData, 0, sourceData.Length, token);
                
                writtenCount++;
                progress?.Report(80 + (int)((writtenCount / (float)filesToWriteIndex.Count) * 20));
            }
        }

        private void WriteVarInt(BinaryWriter bw, int value)
        {
            uint v = (uint)value;
            while (v >= 0x80) { bw.Write((byte)(v | 0x80)); v >>= 7; }
            bw.Write((byte)v);
        }

        public void DecompressArchive(string inputPath, string outputDirectory, byte[]? key = null, IProgress<int>? progress = null)
        {
            using var archive = new GameArchive(inputPath);
            if(key!=null) archive.DecryptionKey = key;

            // Parallel Decompression
            int count = 0;
            Parallel.For(0, archive.FileCount, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i => 
            {
                var entry = archive.GetEntryByIndex(i);
                // Thread-local stream
                using var stream = archive.OpenRead(entry);
                string path = archive.GetPathForAssetId(entry.AssetId) ?? $"{entry.AssetId}.bin";
                string fullPath = Path.Combine(outputDirectory, path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                
                using var outFs = File.Create(fullPath);
                stream.CopyTo(outFs);

                int c = Interlocked.Increment(ref count);
                if (c % 10 == 0) progress?.Report((int)((c / (float)archive.FileCount) * 100));
            });
        }
        
        public bool IsCpuLibraryAvailable() => CodecGDeflate.IsAvailable();
        public PackageInfo InspectPackage(string p) { using var a = new GameArchive(p); return a.GetPackageInfo(); }
        
        public bool VerifyArchive(string p, byte[]? k = null) 
        {
            try
            {
                using var archive = new GameArchive(p);
                if (k != null) archive.DecryptionKey = k;
                byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                int currentFileIndex = -1;
                try {
                    for (int i = 0; i < archive.FileCount; i++) {
                        currentFileIndex = i;
                        using var stream = archive.OpenRead(archive.GetEntryByIndex(i));
                        while (stream.Read(buffer, 0, buffer.Length) > 0) {}
                    }
                }
                catch (Exception e) {
                    Console.WriteLine($"Error at file index {currentFileIndex}: {e.Message}");
                    throw;
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                return true;
            }
            catch (Exception ex) { Console.WriteLine(ex); return false; }
        }

        public async Task CreatePatchArchiveAsync(string baseArch, Dictionary<string,string> map, string outPath, int lvl, byte[]? k, List<DependencyDefinition>? deps, CancellationToken ct)
        {
            await CompressFilesToArchiveAsync(map, outPath, true, lvl, k, false, deps, null, ct);
        }
    }
}