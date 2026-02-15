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
        private const int ChunkSize = 65536; // 64KB Blocks for O(1) random access
        private const int DefaultAlignment = 16;
        private const int GpuAlignment = 4096;

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
            public byte[]? CompressedData;
            public uint Flags;
            public int Alignment;
            public uint Meta1;
            public uint Meta2;
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

        public async ValueTask CompressFilesToArchiveAsync(
            IDictionary<string, string> fileMap,
            string outputPath,
            bool enableDedup,
            int level,
            byte[]? key,
            bool enableMipSplit,
            List<AssetPacker.DependencyDefinition>? dependencies,
            IProgress<int>? progress,
            CancellationToken token,
            CompressionMethod forceMethod = CompressionMethod.Auto)
        {
            CodecGDeflate.IsAvailable();
            bool zstdAvailable = CodecZstd.IsAvailable();
            bool lz4Available = CodecLZ4.IsAvailable();

            var processedFiles = new ConcurrentBag<ProcessedFile>();
            int processedCount = 0;

            await Parallel.ForEachAsync(fileMap, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, async (kvp, ct) =>
            {
                await ProcessFile(kvp.Key, kvp.Value, level, key, enableMipSplit, zstdAvailable, lz4Available, forceMethod, processedFiles, ct).ConfigureAwait(false);
                int c = Interlocked.Increment(ref processedCount);
                progress?.Report((int)((c / (float)fileMap.Count) * 80));
            }).ConfigureAwait(false);

            var sortedFiles = processedFiles.OrderBy(f => f.AssetId).ToList();
            await WriteArchive(sortedFiles, outputPath, enableDedup, progress, token).ConfigureAwait(false);
        }

        private async ValueTask ProcessFile(
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
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();

            CompressionMethod method = forceMethod;
            if (method == CompressionMethod.Auto)
            {
                if (ext == ".dds" || ext == ".model") method = CompressionMethod.GDeflate;
                else method = zstdAvailable ? CompressionMethod.Zstd : CompressionMethod.Store;
            }

            uint m1 = 0, m2 = 0;
            byte[]? finalBuffer = null;

            using (var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                if (ext == ".dds")
                {
                    byte[] raw = new byte[fileSize];
                    await fs.ReadExactlyAsync(raw, ct).ConfigureAwait(false);
                    var h = DdsUtils.GetHeaderInfo(raw);
                    if (h.HasValue)
                    {
                        m1 = ((uint)h.Value.Width << 16) | (uint)h.Value.Height;
                        m2 = ((uint)h.Value.MipCount << 8);
                        if (mipSplit)
                        {
                            int tailSize;
                            finalBuffer = DdsUtils.ProcessTextureForStreaming(raw, out tailSize);
                            m2 = (m2 & 0xFF000000) | ((uint)tailSize & 0x00FFFFFF);
                        }
                        else finalBuffer = raw;
                    }
                    else finalBuffer = raw;
                }

                if (finalBuffer == null)
                {
                    finalBuffer = await CompressToChunksAsync(fs, fileSize, level, key, method, ct).ConfigureAwait(false);
                }
                else
                {
                    using var ms = new MemoryStream(finalBuffer);
                    finalBuffer = await CompressToChunksAsync(ms, finalBuffer.Length, level, key, method, ct).ConfigureAwait(false);
                }
            }

            uint flags = GameArchive.FLAG_STREAMING;
            if (method != CompressionMethod.Store) flags |= GameArchive.FLAG_IS_COMPRESSED;
            if (key != null) flags |= GameArchive.FLAG_ENCRYPTED;
            if (ext == ".dds") flags |= GameArchive.TYPE_TEXTURE;

            flags |= method switch {
                CompressionMethod.Zstd => GameArchive.METHOD_ZSTD,
                CompressionMethod.LZ4 => GameArchive.METHOD_LZ4,
                CompressionMethod.GDeflate => GameArchive.METHOD_GDEFLATE,
                _ => GameArchive.METHOD_STORE
            };

            int alignment = (method == CompressionMethod.GDeflate) ? GpuAlignment : DefaultAlignment;
            int alignPower = 0; int tempAlign = alignment;
            while(tempAlign > 1) { tempAlign >>= 1; alignPower++; }
            flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);

            outBag.Add(new ProcessedFile {
                AssetId = AssetIdGenerator.Generate(relPath),
                OriginalPath = relPath,
                OriginalSize = (uint)fileSize,
                CompressedSize = (uint)finalBuffer.Length,
                CompressedData = finalBuffer,
                Flags = flags,
                Alignment = alignment,
                Meta1 = m1, Meta2 = m2
            });
        }

        private async ValueTask<byte[]> CompressToChunksAsync(Stream input, long totalSize, int level, byte[]? key, CompressionMethod method, CancellationToken ct)
        {
            int blockCount = (int)((totalSize + ChunkSize - 1) / ChunkSize);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(blockCount);
            long tableStart = ms.Position;
            ms.Seek(blockCount * 8, SeekOrigin.Current);

            var entries = new List<GameArchive.ChunkHeaderEntry>();
            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try
            {
                while (true)
                {
                    int read = await input.ReadAsync(chunkBuffer, 0, ChunkSize, ct).ConfigureAwait(false);
                    if (read == 0) break;

                    byte[] chunkData = new byte[read];
                    Array.Copy(chunkBuffer, chunkData, read);

                    byte[] processed = method switch {
                        CompressionMethod.Zstd => CompressZstd(chunkData, level),
                        CompressionMethod.LZ4 => CompressLZ4(chunkData, level),
                        CompressionMethod.GDeflate => CompressGDeflate(chunkData, level),
                        _ => chunkData
                    };

                    if (key != null) processed = Encrypt(processed, key);

                    entries.Add(new GameArchive.ChunkHeaderEntry { CompressedSize = (uint)processed.Length, OriginalSize = (uint)read });
                    bw.Write(processed);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(chunkBuffer); }

            long endPos = ms.Position;
            ms.Position = tableStart;
            foreach (var e in entries) { bw.Write(e.CompressedSize); bw.Write(e.OriginalSize); }
            ms.Position = endPos;

            return ms.ToArray();
        }

        private byte[] CompressGDeflate(byte[] input, int level) {
            ulong bound = CodecGDeflate.CompressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = bound;
                    if (!CodecGDeflate.Compress(pOut, ref outSize, pIn, (ulong)input.Length, (uint)level, 0)) return input;
                    Array.Resize(ref output, (int)outSize); return output;
                }
            }
        }

        private byte[] CompressZstd(byte[] input, int level) {
            ulong bound = CodecZstd.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    ulong outSize = CodecZstd.ZSTD_compress((IntPtr)pOut, bound, (IntPtr)pIn, (ulong)input.Length, level);
                    if (CodecZstd.ZSTD_isError(outSize) != 0) return input;
                    Array.Resize(ref output, (int)outSize); return output;
                }
            }
        }

        private byte[] CompressLZ4(byte[] input, int level) {
            int bound = CodecLZ4.LZ4_compressBound(input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed(byte* pIn = input) fixed(byte* pOut = output) {
                    int outSize = (level < 3)
                        ? CodecLZ4.LZ4_compress_default((IntPtr)pIn, (IntPtr)pOut, input.Length, bound)
                        : CodecLZ4.LZ4_compress_HC((IntPtr)pIn, (IntPtr)pOut, input.Length, bound, level);
                    if (outSize <= 0) return input;
                    Array.Resize(ref output, outSize); return output;
                }
            }
        }

        private byte[] Encrypt(byte[] data, byte[] key) {
            byte[] output = new byte[28 + data.Length];
            var spanOut = new Span<byte>(output);
            RandomNumberGenerator.Fill(spanOut.Slice(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(spanOut.Slice(0, 12), data, spanOut.Slice(28, data.Length), spanOut.Slice(12, 16));
            return output;
        }

        private async ValueTask WriteArchive(List<ProcessedFile> files, string outputPath, bool enableDedup, IProgress<int>? progress, CancellationToken token) {
            long headerSize = 64;
            long fileTableOffset = headerSize;
            long nameTableOffset = fileTableOffset + (files.Count * 44);
            long nameTableFullSize = files.Sum(f => 16 + Encoding.UTF8.GetByteCount(f.OriginalPath) + 5);
            long dataStart = (nameTableOffset + nameTableFullSize + 4095) & ~4095;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes(GameArchive.MagicStr)); bw.Write(GameArchive.Version);
            bw.Write(files.Count); bw.Write(0); bw.Write(0); bw.Write(0);
            bw.Write(fileTableOffset); bw.Write(0L); bw.Write(nameTableOffset); bw.Write(0L);
            while(bw.BaseStream.Position < 64) bw.Write((byte)0);

            long currentOffset = dataStart;
            var contentMap = new Dictionary<ulong, long>();
            long[] finalOffsets = new long[files.Count];

            for(int i=0; i<files.Count; i++) {
                var f = files[i];
                if (enableDedup) {
                    ulong hash = XxHash64.Compute(f.CompressedData!);
                    if (contentMap.TryGetValue(hash, out long existing) && (existing % f.Alignment == 0)) {
                        finalOffsets[i] = existing;
                    } else {
                        currentOffset = (currentOffset + (f.Alignment - 1)) & ~(f.Alignment - 1);
                        finalOffsets[i] = currentOffset; contentMap[hash] = currentOffset;
                        currentOffset += f.CompressedData!.Length;
                    }
                } else {
                    currentOffset = (currentOffset + (f.Alignment - 1)) & ~(f.Alignment - 1);
                    finalOffsets[i] = currentOffset; currentOffset += f.CompressedData!.Length;
                }
            }

            for(int i=0; i<files.Count; i++) {
                var f = files[i]; bw.Write(f.AssetId.ToByteArray()); bw.Write(finalOffsets[i]);
                bw.Write(f.CompressedSize); bw.Write(f.OriginalSize); bw.Write(f.Flags);
                bw.Write(f.Meta1); bw.Write(f.Meta2);
            }

            foreach(var f in files) {
                bw.Write(f.AssetId.ToByteArray());
                byte[] name = Encoding.UTF8.GetBytes(f.OriginalPath);
                uint len = (uint)name.Length;
                while(len >= 0x80) { bw.Write((byte)(len | 0x80)); len >>= 7; } bw.Write((byte)len);
                bw.Write(name);
            }

            long written = 0;
            var filesToWrite = files.Select((f, idx) => (f, idx)).Where(x => finalOffsets[x.idx] >= bw.BaseStream.Position).OrderBy(x => finalOffsets[x.idx]);

            foreach(var item in filesToWrite) {
                long pad = finalOffsets[item.idx] - bw.BaseStream.Position;
                if (pad > 0) bw.Write(new byte[pad]);
                bw.Write(item.f.CompressedData!);
                written++; progress?.Report(80 + (int)((written / (float)files.Count) * 20));
            }
            bw.Flush();
        }

        // --- CLI & GUI Missing Methods ---

        public PackageInfo InspectPackage(string path)
        {
            using var arch = new GameArchive(path);
            return arch.GetPackageInfo();
        }

        public async Task DecompressArchiveAsync(string archivePath, string outputDir, byte[]? key, IProgress<int>? progress = null)
        {
            using var archive = new GameArchive(archivePath) { DecryptionKey = key };
            var info = archive.GetPackageInfo();
            Directory.CreateDirectory(outputDir);

            int count = 0;
            foreach (var entryInfo in info.Entries)
            {
                if (archive.TryGetEntry(entryInfo.AssetId, out var entry))
                {
                    string outPath = Path.Combine(outputDir, entryInfo.Path);
                    string? dir = Path.GetDirectoryName(outPath);
                    if (dir != null) Directory.CreateDirectory(dir);

                    using var source = archive.OpenRead(entry);
                    using var dest = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
                    await source.CopyToAsync(dest);
                }
                count++;
                progress?.Report((int)((count / (float)info.FileCount) * 100));
            }
        }

        public bool VerifyArchive(string path, byte[]? key)
        {
            try
            {
                using var arch = new GameArchive(path) { DecryptionKey = key };
                for (int i = 0; i < arch.FileCount; i++)
                {
                    var entry = arch.GetEntryByIndex(i);
                    using var stream = arch.OpenRead(entry);
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
                    try { while (stream.Read(buffer, 0, buffer.Length) > 0) ; }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                return true;
            }
            catch { return false; }
        }

        public bool IsCpuLibraryAvailable() => CodecGDeflate.IsAvailable();

        public class DependencyDefinition { public string SourcePath = ""; public string TargetPath = ""; public GameArchive.DependencyType Type; }
    }
}
