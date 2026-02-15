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
        private const long LargeFileThreshold = 250 * 1024 * 1024; // 250MB

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
            public byte[]? StreamingData;
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

        public async ValueTask CompressFilesToArchiveAsync(
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

            await Parallel.ForEachAsync(fileMap, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount), CancellationToken = token }, async (kvp, ct) =>
            {
                try {
                    await ProcessFile(kvp.Key, kvp.Value, level, key, enableMipSplit, zstdAvailable, lz4Available, forceMethod, processedFiles, ct).ConfigureAwait(false);
                } catch (Exception ex) {
                    throw new Exception($"Failed to process {kvp.Value}: {ex.Message}", ex);
                }
                int c = Interlocked.Increment(ref processedCount);
                progress?.Report((int)((c / (float)fileMap.Count) * 80));
            }).ConfigureAwait(false);

            var sortedFiles = processedFiles.Where(f => f != null).OrderBy(f => f.AssetId).ToList();

            var depEntries = dependencies?.Select(d => new GameArchive.DependencyEntry {
                SourceAssetId = AssetIdGenerator.Generate(d.SourcePath),
                TargetAssetId = AssetIdGenerator.Generate(d.TargetPath),
                Type = d.Type
            }).ToList() ?? new List<GameArchive.DependencyEntry>();

            await WriteArchive(sortedFiles, depEntries, outputPath, enableDedup, progress, token).ConfigureAwait(false);
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
            bool useStreaming = fileSize >= LargeFileThreshold;

            if (useStreaming)
            {
                using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous);
                var method = (forceMethod == CompressionMethod.Auto) ? CompressionMethod.Zstd : forceMethod;
                if (method == CompressionMethod.Auto) method = CompressionMethod.Zstd;

                byte[] streamedBlob = await CompressStreamingAsync(fs, fileSize, level, key, method, ct).ConfigureAwait(false);
                uint flags = GameArchive.FLAG_STREAMING;
                if (method != CompressionMethod.Store) flags |= GameArchive.FLAG_IS_COMPRESSED;

                flags |= method switch {
                    CompressionMethod.Zstd => GameArchive.METHOD_ZSTD,
                    CompressionMethod.LZ4 => GameArchive.METHOD_LZ4,
                    CompressionMethod.GDeflate => GameArchive.METHOD_GDEFLATE,
                    _ => GameArchive.METHOD_STORE
                };
                if (key != null) flags |= GameArchive.FLAG_ENCRYPTED;

                var pf = new ProcessedFile {
                    AssetId = AssetIdGenerator.Generate(relPath),
                    OriginalPath = relPath, OriginalSize = (uint)fileSize,
                    StreamingData = streamedBlob,
                    Alignment = (method == CompressionMethod.GDeflate) ? GpuAlignment : DefaultAlignment,
                    Flags = flags
                };
                int alignPower = 0; int tempAlign = pf.Alignment;
                while(tempAlign > 1) { tempAlign >>= 1; alignPower++; }
                pf.Flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);
                pf.CompressedSize = (uint)pf.StreamingData.Length;
                outBag.Add(pf);
                return;
            }

            byte[] fileBuffer = ArrayPool<byte>.Shared.Rent((int)fileSize);
            try {
                using (var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan | FileOptions.Asynchronous)) {
                    await fs.ReadExactlyAsync(fileBuffer, 0, (int)fileSize, ct).ConfigureAwait(false);
                }
                var dataSpan = new Span<byte>(fileBuffer, 0, (int)fileSize);
                string ext = Path.GetExtension(inputPath).ToLowerInvariant();
                bool useGDeflate = false, useZstd = false, useLz4 = false;

                if (forceMethod == CompressionMethod.Auto) {
                    useGDeflate = (ext == ".dds" || ext == ".model" || ext == ".geom");
                    if (!useGDeflate) useZstd = zstdAvailable;
                } else {
                    useGDeflate = forceMethod == CompressionMethod.GDeflate;
                    useZstd = forceMethod == CompressionMethod.Zstd;
                    useLz4 = forceMethod == CompressionMethod.LZ4;
                }

                uint m1 = 0, m2 = 0; int tailSize = 0;
                byte[]? processingBuffer = null;

                if (ext == ".dds") {
                    var h = DdsUtils.GetHeaderInfo(dataSpan);
                    if (h.HasValue) {
                        m1 = ((uint)h.Value.Width << 16) | (uint)h.Value.Height;
                        m2 = ((uint)h.Value.MipCount << 8);
                        if (mipSplit) {
                            processingBuffer = DdsUtils.ProcessTextureForStreaming(dataSpan.ToArray(), out tailSize);
                            m2 = (m2 & 0xFF000000) | ((uint)tailSize & 0x00FFFFFF);
                        }
                    }
                }

                if (processingBuffer == null) processingBuffer = dataSpan.ToArray();

                var pf = new ProcessedFile {
                    AssetId = AssetIdGenerator.Generate(relPath), OriginalPath = relPath,
                    OriginalSize = (uint)processingBuffer.Length, Meta1 = m1, Meta2 = m2,
                    Alignment = useGDeflate ? GpuAlignment : DefaultAlignment
                };

                int alignPower = 0; int tempAlign = pf.Alignment;
                while(tempAlign > 1) { tempAlign >>= 1; alignPower++; }
                pf.Flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);

                if (useGDeflate) {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_GDEFLATE;
                    pf.CompressedData = CompressGDeflate(processingBuffer, level);
                } else if (useZstd) {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_ZSTD;
                    pf.CompressedData = CompressZstd(processingBuffer, level);
                } else if (useLz4) {
                    pf.Flags |= GameArchive.FLAG_IS_COMPRESSED | GameArchive.METHOD_LZ4;
                    pf.CompressedData = CompressLZ4(processingBuffer, level);
                } else {
                    pf.Flags |= GameArchive.METHOD_STORE;
                    pf.CompressedData = processingBuffer;
                }

                if (key != null && pf.CompressedData != null) {
                    pf.Flags |= GameArchive.FLAG_ENCRYPTED;
                    pf.CompressedData = Encrypt(pf.CompressedData, key);
                }

                if (tailSize > 0 || ext == ".dds") pf.Flags |= GameArchive.TYPE_TEXTURE;
                pf.CompressedSize = (uint)(pf.CompressedData?.Length ?? 0);
                outBag.Add(pf);
            }
            finally { ArrayPool<byte>.Shared.Return(fileBuffer); }
        }

        private byte[] CompressGDeflate(byte[] input, int level) {
            if (input.Length == 0) return Array.Empty<byte>();
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
            if (input.Length == 0) return Array.Empty<byte>();
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
            if (input.Length == 0) return Array.Empty<byte>();
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

        private async ValueTask<byte[]> CompressStreamingAsync(Stream inputStream, long totalSize, int level, byte[]? key, CompressionMethod method, CancellationToken ct) {
            int blockCount = (int)((totalSize + ChunkSize - 1) / ChunkSize);
            using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
            bw.Write(blockCount); long tableStart = ms.Position;
            ms.Seek(blockCount * 8, SeekOrigin.Current);
            var entries = new List<GameArchive.ChunkHeaderEntry>(blockCount);
            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
            try {
                for (int i = 0; i < blockCount; i++) {
                    int read = await inputStream.ReadAsync(chunkBuffer, 0, ChunkSize, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    byte[] chunkData = new byte[read]; Array.Copy(chunkBuffer, chunkData, read);
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
            } finally { ArrayPool<byte>.Shared.Return(chunkBuffer); }
            long endPos = ms.Position; ms.Position = tableStart;
            foreach (var e in entries) { bw.Write(e.CompressedSize); bw.Write(e.OriginalSize); }
            ms.Position = endPos; return ms.ToArray();
        }

        private byte[] Encrypt(byte[] data, byte[] key) {
            if (data.Length == 0) return Array.Empty<byte>();
            byte[] output = new byte[28 + data.Length]; var spanOut = new Span<byte>(output);
            RandomNumberGenerator.Fill(spanOut.Slice(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(spanOut.Slice(0, 12), new ReadOnlySpan<byte>(data), spanOut.Slice(28, data.Length), spanOut.Slice(12, 16));
            return output;
        }

        private async ValueTask WriteArchive(List<ProcessedFile> files, List<GameArchive.DependencyEntry> dependencies, string outputPath, bool enableDedup, IProgress<int>? progress, CancellationToken token) {
            long headerSize = 64;
            long fileTableSize = files.Count * 44;
            long dependencyTableSize = dependencies.Count * 36;
            long fileTableOffset = headerSize;
            long dependencyTableOffset = fileTableOffset + fileTableSize;
            long nameTableOffset = dependencyTableOffset + dependencyTableSize;
            long nameTableFullSize = files.Sum(f => 16 + Encoding.UTF8.GetByteCount(f.OriginalPath) + 5);
            long dataStart = (nameTableOffset + nameTableFullSize + 4095) & ~4095;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            using var bw = new BinaryWriter(fs);

            bw.Write(Encoding.ASCII.GetBytes(GameArchive.MagicStr)); bw.Write(GameArchive.Version);
            bw.Write(files.Count); bw.Write(0); bw.Write(dependencies.Count); bw.Write(0);
            bw.Write(fileTableOffset); bw.Write(0L); bw.Write(nameTableOffset); bw.Write(dependencyTableOffset);
            while(bw.BaseStream.Position < 64) bw.Write((byte)0);

            long currentOffset = dataStart;
            long[] finalOffsets = new long[files.Count];
            var contentMap = new Dictionary<ulong, long>();
            var filesToWriteIndex = new List<int>();

            for(int i=0; i<files.Count; i++) {
                var f = files[i]; byte[]? sourceData = f.CompressedData ?? f.StreamingData;
                if (sourceData == null || sourceData.Length == 0) { finalOffsets[i] = 0; continue; }
                if (enableDedup) {
                    ulong contentHash = XxHash64.Compute(sourceData);
                    if (contentMap.TryGetValue(contentHash, out long existingOffset) && (existingOffset % f.Alignment) == 0) {
                        finalOffsets[i] = existingOffset;
                    } else {
                        currentOffset = (currentOffset + (f.Alignment - 1)) & ~(f.Alignment - 1);
                        finalOffsets[i] = currentOffset; contentMap[contentHash] = currentOffset;
                        filesToWriteIndex.Add(i); currentOffset += sourceData.Length;
                    }
                } else {
                    currentOffset = (currentOffset + (f.Alignment - 1)) & ~(f.Alignment - 1);
                    finalOffsets[i] = currentOffset; filesToWriteIndex.Add(i); currentOffset += sourceData.Length;
                }
            }

            for(int i=0; i<files.Count; i++) {
                var f = files[i]; bw.Write(f.AssetId.ToByteArray()); bw.Write(finalOffsets[i]);
                bw.Write(f.CompressedSize); bw.Write(f.OriginalSize); bw.Write(f.Flags);
                bw.Write(f.Meta1); bw.Write(f.Meta2);
            }

            foreach(var d in dependencies) {
                bw.Write(d.SourceAssetId.ToByteArray()); bw.Write(d.TargetAssetId.ToByteArray()); bw.Write((uint)d.Type);
            }

            foreach(var f in files) {
                bw.Write(f.AssetId.ToByteArray()); byte[] nameBytes = Encoding.UTF8.GetBytes(f.OriginalPath);
                WriteVarInt(bw, nameBytes.Length); bw.Write(nameBytes);
            }

            // Align to data start
            long bytesToPad = dataStart - bw.BaseStream.Position;
            if (bytesToPad > 0) {
                byte[] pad = new byte[65536];
                while (bytesToPad > 0) { int w = (int)Math.Min(bytesToPad, pad.Length); bw.Write(pad, 0, w); bytesToPad -= w; }
            }

            int writtenCount = 0;
            foreach(int i in filesToWriteIndex) {
                var f = files[i]; long padLen = finalOffsets[i] - bw.BaseStream.Position;
                if(padLen > 0) {
                    byte[] pad = new byte[4096];
                    while(padLen > 0) { int w=(int)Math.Min(padLen, pad.Length); bw.Write(pad, 0, w); padLen-=w; }
                }
                byte[]? sourceData = f.CompressedData ?? f.StreamingData;
                if (sourceData != null && sourceData.Length > 0) bw.Write(sourceData);

                writtenCount++; progress?.Report(80 + (int)((writtenCount / (float)filesToWriteIndex.Count) * 20));
            }
            bw.Flush();
            await fs.FlushAsync(token).ConfigureAwait(false);
        }

        private void WriteVarInt(BinaryWriter bw, int value) {
            uint v = (uint)value; while (v >= 0x80) { bw.Write((byte)(v | 0x80)); v >>= 7; } bw.Write((byte)v);
        }

        public async ValueTask DecompressArchiveAsync(string inputPath, string outputDirectory, byte[]? key = null, IProgress<int>? progress = null, CancellationToken token = default) {
            using var archive = new GameArchive(inputPath); if(key!=null) archive.DecryptionKey = key;
            int count = 0; var indices = Enumerable.Range(0, archive.FileCount).ToList();
            await Parallel.ForEachAsync(indices, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, async (i, ct) => {
                var entry = archive.GetEntryByIndex(i); using var stream = archive.OpenRead(entry);
                string path = archive.GetPathForAssetId(entry.AssetId) ?? $"{entry.AssetId}.bin";
                string fullPath = Path.Combine(outputDirectory, path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                using var outFs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await stream.CopyToAsync(outFs, ct).ConfigureAwait(false);
                int c = Interlocked.Increment(ref count); if (c % 10 == 0) progress?.Report((int)((c / (float)archive.FileCount) * 100));
            }).ConfigureAwait(false);
        }

        public bool IsCpuLibraryAvailable() => CodecGDeflate.IsAvailable();
        public PackageInfo InspectPackage(string p) { using var a = new GameArchive(p); return a.GetPackageInfo(); }

        public bool VerifyArchive(string p, byte[]? k = null) {
            try {
                using var archive = new GameArchive(p); if (k != null) archive.DecryptionKey = k;
                byte[] buffer = new byte[128 * 1024];
                for (int i = 0; i < archive.FileCount; i++) {
                    using var stream = archive.OpenRead(archive.GetEntryByIndex(i));
                    while (stream.Read(buffer, 0, buffer.Length) > 0) {}
                }
                return true;
            } catch { return false; }
        }
    }
}
