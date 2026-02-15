using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public enum CompressionMethod { Auto, Store, GDeflate, Zstd, LZ4 }

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
            string root = Path.GetFullPath(sourceDirectory);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                map[file] = Path.GetRelativePath(root, file).Replace('\\', '/');
            return map;
        }

        public async ValueTask CompressFilesToArchiveAsync(IDictionary<string, string> fileMap, string outputPath, bool enableDedup, int level, byte[]? key, bool mipSplit, IProgress<int>? progress, CancellationToken token, CompressionMethod forceMethod = CompressionMethod.Auto)
        {
            var processed = new ConcurrentBag<ProcessedFile>();
            int count = 0;

            await Parallel.ForEachAsync(fileMap, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = token }, async (kvp, ct) => {
                await ProcessFile(kvp.Key, kvp.Value, level, key, mipSplit, forceMethod, processed, ct);
                progress?.Report((int)(Interlocked.Increment(ref count) / (float)fileMap.Count * 80));
            });

            await WriteArchive(processed.OrderBy(f => f.AssetId).ToList(), outputPath, enableDedup, key, progress);
        }

        private async ValueTask ProcessFile(string input, string rel, int level, byte[]? key, bool mipSplit, CompressionMethod force, ConcurrentBag<ProcessedFile> outBag, CancellationToken ct)
        {
            byte[] raw = await File.ReadAllBytesAsync(input, ct);
            CompressionMethod method = force == CompressionMethod.Auto ? (Path.GetExtension(input).ToLower() == ".dds" ? CompressionMethod.GDeflate : CompressionMethod.Zstd) : force;

            uint m1 = 0, m2 = 0;
            if (Path.GetExtension(input).ToLower() == ".dds") {
                var h = DdsUtils.GetHeaderInfo(raw);
                if (h.HasValue) {
                    m1 = ((uint)h.Value.Width << 16) | (uint)h.Value.Height;
                    m2 = (uint)h.Value.MipCount << 8;
                    if (mipSplit) raw = DdsUtils.ProcessTextureForStreaming(raw, out int tail);
                }
            }

            byte[] compressed = await CompressToChunksAsync(raw, level, key, method);

            uint flags = GameArchive.FLAG_STREAMING | (method != CompressionMethod.Store ? GameArchive.FLAG_IS_COMPRESSED : 0);

            // Add method flags
            flags |= method switch
            {
                CompressionMethod.GDeflate => GameArchive.METHOD_GDEFLATE,
                CompressionMethod.Zstd => GameArchive.METHOD_ZSTD,
                CompressionMethod.LZ4 => GameArchive.METHOD_LZ4,
                _ => GameArchive.METHOD_STORE
            };

            if (key != null) flags |= GameArchive.FLAG_ENCRYPTED_META;

            int align = method == CompressionMethod.GDeflate ? GpuAlignment : DefaultAlignment;
            int alignPower = (int)Math.Log2(align);
            flags |= (uint)(alignPower << GameArchive.SHIFT_ALIGNMENT);

            outBag.Add(new ProcessedFile { AssetId = AssetIdGenerator.Generate(rel), OriginalPath = rel, OriginalSize = (uint)raw.Length, CompressedSize = (uint)compressed.Length, CompressedData = compressed, Flags = flags, Alignment = align, Meta1 = m1, Meta2 = m2 });
        }

        private async Task<byte[]> CompressToChunksAsync(byte[] input, int level, byte[]? key, CompressionMethod method)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            int blocks = (input.Length + ChunkSize - 1) / ChunkSize;
            bw.Write(blocks);
            ms.Seek(blocks * 8, SeekOrigin.Current);

            var entries = new List<GameArchive.ChunkHeaderEntry>();
            for (int i = 0; i < blocks; i++) {
                int size = Math.Min(ChunkSize, input.Length - i * ChunkSize);
                byte[] chunk = new byte[size];
                Array.Copy(input, i * ChunkSize, chunk, 0, size);
                byte[] proc = method switch {
                    CompressionMethod.GDeflate => CompressGDeflate(chunk, level),
                    CompressionMethod.Zstd => CompressZstd(chunk, level),
                    CompressionMethod.LZ4 => CompressLZ4(chunk, level),
                    _ => chunk
                };
                entries.Add(new GameArchive.ChunkHeaderEntry { CompressedSize = (uint)proc.Length, OriginalSize = (uint)size });
                bw.Write(proc);
            }

            byte[] table = new byte[blocks * 8];
            for (int i = 0; i < blocks; i++) {
                BitConverter.TryWriteBytes(table.AsSpan(i * 8, 4), entries[i].CompressedSize);
                BitConverter.TryWriteBytes(table.AsSpan(i * 8 + 4, 4), entries[i].OriginalSize);
            }
            if (key != null) table = Encrypt(table, key);

            ms.Position = 4; bw.Write(table);
            return ms.ToArray();
        }

        private byte[] CompressGDeflate(byte[] input, int level) {
            ulong bound = CodecGDeflate.CompressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe { fixed(byte* pI = input, pO = output) { ulong outS = bound; CodecGDeflate.Compress(pO, ref outS, pI, (ulong)input.Length, (uint)level, 0); Array.Resize(ref output, (int)outS); return output; } }
        }

        private byte[] CompressZstd(byte[] input, int level) {
            ulong bound = CodecZstd.ZSTD_compressBound((ulong)input.Length);
            byte[] output = new byte[bound];
            unsafe { fixed(byte* pI = input, pO = output) { ulong outS = CodecZstd.ZSTD_compress((IntPtr)pO, bound, (IntPtr)pI, (ulong)input.Length, level); Array.Resize(ref output, (int)outS); return output; } }
        }

        private byte[] CompressLZ4(byte[] input, int level) {
            int bound = CodecLZ4.LZ4_compressBound(input.Length);
            byte[] output = new byte[bound];
            unsafe {
                fixed (byte* pI = input, pO = output) {
                    int outS = level > 3
                        ? CodecLZ4.LZ4_compress_HC((IntPtr)pI, (IntPtr)pO, input.Length, bound, level)
                        : CodecLZ4.LZ4_compress_default((IntPtr)pI, (IntPtr)pO, input.Length, bound);
                    if (outS <= 0) return input;
                    Array.Resize(ref output, outS);
                    return output;
                }
            }
        }

        private byte[] Encrypt(byte[] data, byte[] key) {
            byte[] output = new byte[28 + data.Length];
            RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(output.AsSpan(0, 12), data, output.AsSpan(28), output.AsSpan(12, 16));
            return output;
        }

        private async Task WriteArchive(List<ProcessedFile> files, string path, bool dedup, byte[]? key, IProgress<int>? progress)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            // 1. Calculate table offsets
            long fileTableOffset = 64;
            long nameTableOffset = fileTableOffset + (files.Count * 44);
            long dataStartOffset = (nameTableOffset + files.Sum(f => 16 + 2 + Encoding.UTF8.GetByteCount(f.OriginalPath)) + 4095) & ~4095;

            // 2. Write Header
            bw.Write(0x4B435047); // "GPCK" in little-endian
            bw.Write(1); // Version
            bw.Write(files.Count);
            bw.Write(0); // Padding
            bw.Write(fileTableOffset);
            bw.Write(nameTableOffset);
            bw.Seek(64, SeekOrigin.Begin);

            // 3. Pre-calculate offsets with deduplication
            var contentMap = new Dictionary<ulong, long>();
            var uniqueDataToWrite = new List<(ProcessedFile File, long Offset)>();
            long currentDataPtr = dataStartOffset;

            var finalEntries = new List<(ProcessedFile File, long Offset)>();

            foreach (var f in files)
            {
                long offset;
                if (dedup)
                {
                    ulong hash = XxHash64.Compute(f.CompressedData!);
                    if (contentMap.TryGetValue(hash, out long existing))
                    {
                        offset = existing;
                    }
                    else
                    {
                        offset = (currentDataPtr + f.Alignment - 1) & ~(f.Alignment - 1);
                        contentMap[hash] = offset;
                        uniqueDataToWrite.Add((f, offset));
                        currentDataPtr = offset + f.CompressedData!.Length;
                    }
                }
                else
                {
                    offset = (currentDataPtr + f.Alignment - 1) & ~(f.Alignment - 1);
                    uniqueDataToWrite.Add((f, offset));
                    currentDataPtr = offset + f.CompressedData!.Length;
                }
                finalEntries.Add((f, offset));
            }

            // 4. Write File Table
            foreach (var (f, offset) in finalEntries)
            {
                bw.Write(f.AssetId.ToByteArray());
                bw.Write(offset);
                bw.Write(f.CompressedSize);
                bw.Write(f.OriginalSize);
                bw.Write(f.Flags);
                bw.Write(f.Meta1);
                bw.Write(f.Meta2);
            }

            // 5. Write Name Table
            foreach (var f in files)
            {
                bw.Write(f.AssetId.ToByteArray());
                byte[] n = Encoding.UTF8.GetBytes(f.OriginalPath);
                bw.Write((ushort)n.Length);
                bw.Write(n);
            }

            // 6. Write Data Blocks
            progress?.Report(90);
            foreach (var (f, offset) in uniqueDataToWrite)
            {
                fs.Seek(offset, SeekOrigin.Begin);
                await fs.WriteAsync(f.CompressedData!);
            }
            progress?.Report(100);
        }

        public bool IsCpuLibraryAvailable() => CodecGDeflate.IsAvailable();
        public PackageInfo InspectPackage(string path) { using var arch = new GameArchive(path); return arch.GetPackageInfo(); }
        public async Task DecompressArchiveAsync(string path, string outDir, byte[]? key, IProgress<int>? progress) {
            using var arch = new GameArchive(path) { DecryptionKey = key };
            foreach(var e in arch.GetPackageInfo().Entries) {
                if (arch.TryGetEntry(e.AssetId, out var entry)) {
                    string p = Path.Combine(outDir, e.Path); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                    using var s = arch.OpenRead(entry); using var df = File.Create(p); await s.CopyToAsync(df);
                }
            }
        }
        public bool VerifyArchive(string path, byte[]? key) { try { using var arch = new GameArchive(path) { DecryptionKey = key }; for(int i=0; i<arch.FileCount; i++) { using var s = arch.OpenRead(arch.GetEntryByIndex(i)); s.CopyTo(Stream.Null); } return true; } catch { return false; } }
    }
}
