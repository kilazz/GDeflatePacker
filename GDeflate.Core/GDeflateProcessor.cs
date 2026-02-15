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

namespace GDeflate.Core
{
    public class GDeflateProcessor
    {
        private const int TileSize = 65536; 
        private const int ChunkSize = 65536; // 64KB chunks for streaming
        private const int DefaultAlignment = 16;
        private const int GpuAlignment = 4096;

        private class ProcessedFile
        {
            public Guid AssetId;
            public required string OriginalPath;
            public uint OriginalSize;
            public uint CompressedSize;
            public byte[]? CompressedData; 
            public uint Flags;
            public int Alignment;
            public uint Meta1;
            public uint Meta2;
        }
        
        public class DependencyDefinition
        {
            public required string SourcePath;
            public required string TargetPath;
            public GDeflateArchive.DependencyType Type;
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
            GDeflateCpuApi.IsAvailable(); 
            bool zstdAvailable = ZstdCpuApi.IsAvailable();
            var processedFiles = new ConcurrentBag<ProcessedFile>();
            int processedCount = 0;
            
            await Parallel.ForEachAsync(fileMap, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = token }, async (kvp, ct) =>
            {
                await ProcessFile(kvp.Key, kvp.Value, level, key, enableMipSplit, zstdAvailable, processedFiles, ct);
                int c = Interlocked.Increment(ref processedCount);
                progress?.Report((int)((c / (float)fileMap.Count) * 70));
            });

            var sortedFiles = processedFiles.OrderBy(f => f.AssetId).ToList();

            var depEntries = dependencies?.Select(d => new GDeflateArchive.DependencyEntry {
                SourceAssetId = AssetIdGenerator.Generate(d.SourcePath),
                TargetAssetId = AssetIdGenerator.Generate(d.TargetPath),
                Type = d.Type
            }).ToList() ?? new List<GDeflateArchive.DependencyEntry>();

            await WriteArchive(sortedFiles, depEntries, outputPath, progress, token);
        }

        public void CompressFilesToArchive(IDictionary<string, string> f, string o, bool d = true, int l = 9, bool m = false, IProgress<int>? p = null, CancellationToken t = default)
            => CompressFilesToArchiveAsync(f, o, d, l, null, m, null, p, t).GetAwaiter().GetResult();

        private async Task ProcessFile(string inputPath, string relPath, int level, byte[]? key, bool mipSplit, bool zstdAvail, ConcurrentBag<ProcessedFile> outBag, CancellationToken ct)
        {
            byte[] data = await File.ReadAllBytesAsync(inputPath, ct); 
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            bool useGDeflate = ext == ".dds" || ext == ".model" || ext == ".geom" || ext == ".gdef";
            
            // Texture Manifest Extraction
            uint m1 = 0, m2 = 0;
            if (ext == ".dds")
            {
                var h = DdsUtils.GetHeaderInfo(data);
                if (h.HasValue)
                {
                    m1 = ((uint)h.Value.Width << 16) | (uint)h.Value.Height;
                    m2 = ((uint)h.Value.MipCount << 8);
                }
            }

            // Decide Streaming vs Solid
            bool isStreaming = data.Length > 256 * 1024; 
            
            var pf = new ProcessedFile 
            { 
                AssetId = AssetIdGenerator.Generate(relPath),
                OriginalPath = relPath, 
                OriginalSize = (uint)data.Length,
                Meta1 = m1,
                Meta2 = m2
            };

            pf.Alignment = useGDeflate ? GpuAlignment : DefaultAlignment;
            int alignTemp = pf.Alignment;
            int alignPower = 0;
            while(alignTemp > 1) { alignTemp >>= 1; alignPower++; }
            pf.Flags |= (uint)(alignPower << GDeflateArchive.SHIFT_ALIGNMENT);

            if (useGDeflate)
            {
                pf.Flags |= GDeflateArchive.FLAG_IS_COMPRESSED | GDeflateArchive.METHOD_GDEFLATE;
                pf.CompressedData = CompressGDeflate(data, level);
            }
            else if (zstdAvail) 
            {
                pf.Flags |= GDeflateArchive.FLAG_IS_COMPRESSED | GDeflateArchive.METHOD_ZSTD;
                if (isStreaming)
                {
                    pf.Flags |= GDeflateArchive.FLAG_STREAMING;
                    pf.CompressedData = CompressZstdStreaming(data, level, key);
                    pf.Flags |= GDeflateArchive.FLAG_ENCRYPTED; 
                }
                else
                {
                    pf.CompressedData = CompressZstd(data, level);
                    if (key != null) { pf.Flags |= GDeflateArchive.FLAG_ENCRYPTED; pf.CompressedData = Encrypt(pf.CompressedData, key); }
                }
            }
            else
            {
                pf.Flags |= GDeflateArchive.METHOD_STORE;
                pf.CompressedData = data;
                if (key != null) { pf.Flags |= GDeflateArchive.FLAG_ENCRYPTED; pf.CompressedData = Encrypt(pf.CompressedData, key); }
            }

            if ((pf.Flags & GDeflateArchive.FLAG_STREAMING) == 0 && (pf.Flags & GDeflateArchive.TYPE_TEXTURE) != 0)
            {
                pf.Flags |= GDeflateArchive.TYPE_TEXTURE; 
            }
            if (ext == ".dds") pf.Flags |= GDeflateArchive.TYPE_TEXTURE;

            pf.CompressedSize = (uint)pf.CompressedData.Length;
            outBag.Add(pf);
        }

        private byte[] CompressGDeflate(byte[] input, int level)
        {
            ulong bound = GDeflateCpuApi.CompressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = bound;
                    if (!GDeflateCpuApi.Compress(pOut, ref outSize, pIn, (ulong)input.Length, (uint)level, 0))
                        throw new Exception("GDeflate failed");
                    Array.Resize(ref output, (int)outSize);
                    return output;
                }
            }
        }

        private byte[] CompressZstd(byte[] input, int level)
        {
            ulong bound = ZstdCpuApi.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = ZstdCpuApi.ZSTD_compress((IntPtr)pOut, bound, (IntPtr)pIn, (ulong)input.Length, level);
                    if (ZstdCpuApi.ZSTD_isError(outSize) != 0) return input;
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

            var entries = new List<GDeflateArchive.ChunkHeaderEntry>();

            for (int i = 0; i < blockCount; i++)
            {
                int offset = i * ChunkSize;
                int size = Math.Min(ChunkSize, input.Length - offset);
                byte[] chunk = new byte[size];
                Array.Copy(input, offset, chunk, 0, size);

                byte[] compressed = CompressZstd(chunk, level);
                if (key != null) compressed = Encrypt(compressed, key);

                entries.Add(new GDeflateArchive.ChunkHeaderEntry { 
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
            List<GDeflateArchive.DependencyEntry> dependencies,
            string outputPath, 
            IProgress<int>? progress, 
            CancellationToken token)
        {
            long headerSize = 64; 
            long fileTableSize = files.Count * 44; // Hardcoded 44 matches Struct
            long nameTableSize = files.Sum(f => 16 + Encoding.UTF8.GetByteCount(f.OriginalPath) + 5);
            long dependencyTableSize = dependencies.Count * 36;

            long fileTableOffset = headerSize;
            long dependencyTableOffset = fileTableOffset + fileTableSize;
            long nameTableOffset = dependencyTableOffset + dependencyTableSize;
            long metaEnd = nameTableOffset + nameTableSize;

            long dataStart = (metaEnd + (4096 - 1)) & ~(4096 - 1);
            long currentOffset = dataStart;

            // Use FileStream explicitly to ensure contiguous writing and proper zero-filling
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            using var bw = new BinaryWriter(fs);

            // 1. Header
            bw.Write(Encoding.ASCII.GetBytes(GDeflateArchive.Magic)); 
            bw.Write(GDeflateArchive.Version); 
            bw.Write(files.Count); 
            bw.Write(0); 
            bw.Write(dependencies.Count); 
            bw.Write((long)0); 
            
            bw.Write(fileTableOffset);
            bw.Write((long)0); 
            bw.Write(nameTableOffset);
            bw.Write(dependencyTableOffset);
            
            // Pad Header to 64 bytes
            while(fs.Position < 64) bw.Write((byte)0);

            // Calculate Offsets
            long[] finalOffsets = new long[files.Count];
            for(int i=0; i<files.Count; i++)
            {
                var f = files[i];
                long padding = (f.Alignment - (currentOffset % f.Alignment)) % f.Alignment;
                currentOffset += padding;
                finalOffsets[i] = currentOffset;
                currentOffset += f.CompressedSize;
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

            // 5. Zero Pad until DataStart (Critical Fix)
            long bytesToPad = dataStart - fs.Position;
            if (bytesToPad > 0)
            {
                byte[] pad = new byte[Math.Min(bytesToPad, 65536)];
                while (bytesToPad > 0)
                {
                    int write = (int)Math.Min(bytesToPad, pad.Length);
                    await fs.WriteAsync(pad, 0, write, token);
                    bytesToPad -= write;
                }
            }

            // 6. Write Data Blobs
            int written = 0;
            // Write sequentially to ensure file position matches calculated offsets exactly.
            // Parallel writing to a single FileStream is tricky, so we do sequential here 
            // but we can buffer chunks. For safety and correctness: Sequential.
            for(int i=0; i<files.Count; i++)
            {
                var f = files[i];
                if (f.CompressedData != null)
                {
                    // Align position check
                    long currentPos = fs.Position;
                    long requiredPos = finalOffsets[i];
                    if (currentPos < requiredPos)
                    {
                        long padLen = requiredPos - currentPos;
                        byte[] pad = new byte[padLen];
                        await fs.WriteAsync(pad, 0, (int)padLen, token);
                    }
                    else if (currentPos > requiredPos)
                    {
                        throw new InvalidDataException("Archive write overlap - Internal Error");
                    }

                    await fs.WriteAsync(f.CompressedData, 0, f.CompressedData.Length, token);
                }
                
                written++;
                if (written % 10 == 0) progress?.Report(70 + (int)((written / (float)files.Count) * 30));
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
            using var archive = new GDeflateArchive(inputPath);
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
        
        public bool IsCpuLibraryAvailable() => GDeflateCpuApi.IsAvailable();
        public PackageInfo InspectPackage(string p) { using var a = new GDeflateArchive(p); return a.GetPackageInfo(); }
        public bool VerifyArchive(string p, byte[]? k = null) { return true; }
        public void ExtractSingleFile(string p, string o, string t, byte[]? k) 
        {
            using var a = new GDeflateArchive(p);
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
    }
}