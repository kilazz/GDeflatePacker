using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace GDeflate.Core
{
    /// <summary>
    /// Standard GDeflate Processor.
    /// Implements Chunked Streaming compression.
    /// Updated to support DirectStorage compliant "Pure Data" layout for packages.
    /// </summary>
    public class GDeflateProcessor
    {
        // 64KB is the standard page size for GPU decompression scenarios
        private const int ChunkSize = 65536;

        public bool IsCpuLibraryAvailable() => GDeflateCpuApi.IsAvailable();

        #region Public API

        public void CompressFilesToArchive(IDictionary<string, string> fileMap, string outputPath, string format, IProgress<int>? progress = null, CancellationToken token = default)
        {
            EnsureBackend();

            if (format.Equals(".gpck", StringComparison.OrdinalIgnoreCase))
            {
                CompressGamePackage(fileMap, outputPath, progress, token);
            }
            else if (format.Equals(".gdef", StringComparison.OrdinalIgnoreCase))
            {
                if (fileMap.Count != 1)
                    throw new ArgumentException("GDEF format only supports single file compression.");

                foreach (var kvp in fileMap)
                {
                    CompressFileChunked(kvp.Key, outputPath, progress, token);
                    return;
                }
            }
            else
            {
                throw new NotSupportedException($"Format {format} is not supported.");
            }
        }

        public void DecompressArchive(string inputPath, string outputDirectory, IProgress<int>? progress = null, CancellationToken token = default)
        {
            EnsureBackend();
            string ext = Path.GetExtension(inputPath).ToLower();

            if (ext == ".gdef")
            {
                string outputName = Path.GetFileNameWithoutExtension(inputPath);
                string fullOutputPath = Path.Combine(outputDirectory, outputName);
                DecompressFileChunked(inputPath, fullOutputPath, progress, token);
                progress?.Report(100);
            }
            else if (ext == ".gpck")
            {
                ExtractGamePackage(inputPath, outputDirectory, progress, token);
            }
            else
            {
                throw new NotSupportedException($"Format {ext} is not supported.");
            }
        }

        public PackageInfo InspectPackage(string packagePath)
        {
            using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            var info = new PackageInfo { FilePath = packagePath, TotalSize = fs.Length };

            // Read Header
            byte[] magic = br.ReadBytes(4);
            string magicStr = Encoding.ASCII.GetString(magic);
            info.Magic = magicStr;

            if (magicStr != "GPCK") return info;

            info.Version = br.ReadInt32();
            long directoryOffset = br.ReadInt64();

            // Read Directory
            fs.Position = directoryOffset;
            int fileCount = br.ReadInt32();
            info.FileCount = fileCount;

            for (int i = 0; i < fileCount; i++)
            {
                int pathLen = br.ReadInt32();
                byte[] pathBytes = br.ReadBytes(pathLen);
                string path = Encoding.UTF8.GetString(pathBytes);
                long offset = br.ReadInt64();
                long origSize = br.ReadInt64();

                int chunkCount = br.ReadInt32();
                long compressedSize = 0;
                for (int c = 0; c < chunkCount; c++)
                {
                    compressedSize += br.ReadInt32();
                }

                bool isAligned = (offset % 4096) == 0;

                info.Entries.Add(new PackageEntryInfo
                {
                    Path = path,
                    Offset = offset,
                    OriginalSize = origSize,
                    CompressedSize = compressedSize,
                    Is4KAligned = isAligned,
                    ChunkCount = chunkCount
                });
            }

            return info;
        }

        #endregion

        #region Helper Methods

        private void PadToAlignment(Stream stream, int alignment)
        {
            long currentOffset = stream.Position;
            long padding = (alignment - (currentOffset % alignment)) % alignment;
            if (padding > 0)
            {
                stream.Write(new byte[padding]);
            }
        }

        #endregion

        #region Chunked Operations (Streaming)

        /// <summary>
        /// Compresses a stream in 64KB chunks.
        /// Returns the list of compressed chunk sizes (bytes).
        /// if `writeInlineHeaders` is true, it writes [CompSize][OrigSize] before data (for .gdef).
        /// if `writeInlineHeaders` is false, it writes RAW data only (for .gpck).
        /// </summary>
        private unsafe List<int> StreamCompress(Stream fsIn, Stream fsOut, ulong inputSize, CancellationToken token, bool writeInlineHeaders)
        {
            void* pInput = null;
            void* pOutput = null;
            var chunkSizes = new List<int>();

            try
            {
                // Allocate buffers once
                pInput = NativeMemory.Alloc(ChunkSize);
                ulong bound = GDeflateCpuApi.CompressBound(ChunkSize);
                pOutput = NativeMemory.Alloc((nuint)bound);

                byte[] headerBuffer = new byte[8]; // [CompSize(4)][OrigSize(4)]
                long remaining = (long)inputSize;

                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();

                    // 1. Read Chunk
                    int bytesToRead = (int)Math.Min(ChunkSize, remaining);
                    var inputSpan = new Span<byte>(pInput, bytesToRead);
                    int bytesRead = fsIn.Read(inputSpan);

                    if (bytesRead == 0) break;

                    // 2. Compress Chunk
                    ulong compressedSize = bound;
                    bool success = GDeflateCpuApi.Compress(pOutput, ref compressedSize, pInput, (ulong)bytesRead, 12, 0);

                    if (!success) throw new Exception("GDeflate chunk compression failed.");

                    chunkSizes.Add((int)compressedSize);

                    // 3. Write Output
                    if (writeInlineHeaders)
                    {
                        // Legacy/Single File Mode: Needs headers to describe itself
                        BitConverter.TryWriteBytes(new Span<byte>(headerBuffer, 0, 4), (int)compressedSize);
                        BitConverter.TryWriteBytes(new Span<byte>(headerBuffer, 4, 4), bytesRead);
                        fsOut.Write(headerBuffer);
                    }

                    // Write Compressed Data
                    var outputSpan = new ReadOnlySpan<byte>(pOutput, (int)compressedSize);
                    fsOut.Write(outputSpan);

                    remaining -= bytesRead;
                }
            }
            finally
            {
                if (pInput != null) NativeMemory.Free(pInput);
                if (pOutput != null) NativeMemory.Free(pOutput);
            }

            return chunkSizes;
        }

        private unsafe void CompressFileChunked(string inputFile, string outputFile, IProgress<int>? progress, CancellationToken token)
        {
            using var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024);
            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write);

            // File Header
            // Magic (GCHK) + Version (1) + TotalOriginalSize (8)
            byte[] header = new byte[16];
            Encoding.ASCII.GetBytes("GCHK").CopyTo(header, 0);
            BitConverter.TryWriteBytes(new Span<byte>(header, 4, 4), 1);
            BitConverter.TryWriteBytes(new Span<byte>(header, 8, 8), (ulong)fsIn.Length);
            fsOut.Write(header);

            // Stream Body with Inline Headers
            StreamCompress(fsIn, fsOut, (ulong)fsIn.Length, token, true);
        }

        private unsafe void DecompressFileChunked(string inputFile, string outputFile, IProgress<int>? progress, CancellationToken token)
        {
            using var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read);

            // 1. Check Header
            byte[] header = new byte[16];
            int read = fsIn.Read(header, 0, 16);

            long totalSize = 0;

            if (read >= 4 && Encoding.ASCII.GetString(header, 0, 4) == "GCHK")
            {
                totalSize = BitConverter.ToInt64(header, 8); // For progress tracking
            }
            else
            {
                // Fallback to Legacy Single Pass
                fsIn.Seek(0, SeekOrigin.Begin);
                DecompressLegacy(fsIn, outputFile, token);
                return;
            }

            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write);

            void* pInput = null;
            void* pOutput = null;

            try
            {
                ulong maxCompBound = GDeflateCpuApi.CompressBound(ChunkSize);
                pInput = NativeMemory.Alloc((nuint)maxCompBound);
                pOutput = NativeMemory.Alloc(ChunkSize);

                byte[] headerBuffer = new byte[8];
                long totalDecompressed = 0;

                while (fsIn.Position < fsIn.Length)
                {
                    token.ThrowIfCancellationRequested();

                    // Read Chunk Header [CompSize(4)][OrigSize(4)]
                    int hRead = fsIn.Read(headerBuffer, 0, 8);
                    if (hRead == 0) break;
                    if (hRead < 8) throw new EndOfStreamException("Truncated chunk header.");

                    int compSize = BitConverter.ToInt32(headerBuffer, 0);
                    int origSize = BitConverter.ToInt32(headerBuffer, 4);

                    // Read Compressed Data
                    var inputSpan = new Span<byte>(pInput, compSize);
                    int dataRead = fsIn.Read(inputSpan);
                    if (dataRead != compSize) throw new EndOfStreamException("Truncated chunk body.");

                    // Decompress
                    bool success = GDeflateCpuApi.Decompress(pOutput, (ulong)origSize, pInput, (ulong)compSize, 1);
                    if (!success) throw new Exception("Chunk decompression failed.");

                    // Write to Disk
                    var outputSpan = new ReadOnlySpan<byte>(pOutput, origSize);
                    fsOut.Write(outputSpan);

                    totalDecompressed += origSize;
                    if (totalSize > 0) progress?.Report((int)((totalDecompressed / (double)totalSize) * 100));
                }
            }
            finally
            {
                if (pInput != null) NativeMemory.Free(pInput);
                if (pOutput != null) NativeMemory.Free(pOutput);
            }
        }

        private unsafe void DecompressLegacy(FileStream fsIn, string outputFile, CancellationToken token)
        {
            byte[] sizeBuffer = new byte[8];
            fsIn.ReadExactly(sizeBuffer);
            ulong originalSize = BitConverter.ToUInt64(sizeBuffer, 0);

            long compressedPayloadSize = fsIn.Length - 8;
            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write);

            void* pInput = NativeMemory.Alloc((nuint)compressedPayloadSize);
            void* pOutput = NativeMemory.Alloc((nuint)originalSize);

            try
            {
                using (var umsIn = new UnmanagedMemoryStream((byte*)pInput, compressedPayloadSize, compressedPayloadSize, FileAccess.Write))
                {
                    fsIn.CopyTo(umsIn);
                }

                bool success = GDeflateCpuApi.Decompress(pOutput, originalSize, pInput, (ulong)compressedPayloadSize, (uint)Environment.ProcessorCount);
                if (!success) throw new Exception("Legacy decompression failed.");

                using (var umsOut = new UnmanagedMemoryStream((byte*)pOutput, (long)originalSize, (long)originalSize, FileAccess.Read))
                {
                    umsOut.CopyTo(fsOut);
                }
            }
            finally
            {
                NativeMemory.Free(pInput);
                NativeMemory.Free(pOutput);
            }
        }

        #endregion

        #region Game Package Operations (DirectStorage Optimized)

        private struct PackageEntry
        {
            public string Path;
            public long Offset;
            public long OriginalSize;
            public List<int> ChunkSizes; // Store table of chunk sizes for this file
        }

        private unsafe void CompressGamePackage(IDictionary<string, string> fileMap, string outputPath, IProgress<int>? progress, CancellationToken token)
        {
            using var fsPackage = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite);

            // 1. Write Header Placeholder
            // We don't know the DirectoryOffset yet.
            fsPackage.Write(Encoding.ASCII.GetBytes("GPCK")); // 4 bytes
            fsPackage.Write(BitConverter.GetBytes(2));        // 4 bytes: Version 2 (No Inline Headers)
            fsPackage.Write(BitConverter.GetBytes((long)0));  // 8 bytes: Directory Offset placeholder

            // Align to 4096 before first file
            PadToAlignment(fsPackage, 4096);

            var entries = new List<PackageEntry>();
            int current = 0;

            foreach (var kvp in fileMap)
            {
                token.ThrowIfCancellationRequested();

                string inputFile = kvp.Key;
                string relativePath = kvp.Value;
                long fileStartOffset = fsPackage.Position;
                long originalSize = new FileInfo(inputFile).Length;

                List<int> chunkSizes;

                if (originalSize > 0)
                {
                    using (var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // Write RAW compressed data (no inline headers)
                        chunkSizes = StreamCompress(fsIn, fsPackage, (ulong)originalSize, token, false);
                    }
                }
                else
                {
                    chunkSizes = new List<int>();
                }

                entries.Add(new PackageEntry
                {
                    Path = relativePath,
                    Offset = fileStartOffset,
                    OriginalSize = originalSize,
                    ChunkSizes = chunkSizes
                });

                // Post-file padding (Align next file to 4K)
                PadToAlignment(fsPackage, 4096);

                current++;
                progress?.Report((int)((current / (float)fileMap.Count) * 100));
            }

            // 2. Write Directory (Metadata) at the end of the file
            long directoryOffset = fsPackage.Position;
            using var bw = new BinaryWriter(fsPackage, Encoding.UTF8, true);

            bw.Write(entries.Count);

            foreach (var entry in entries)
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(entry.Path);
                bw.Write(pathBytes.Length);
                bw.Write(pathBytes);
                bw.Write(entry.Offset);
                bw.Write(entry.OriginalSize);

                // Write Chunk Table for this file
                bw.Write(entry.ChunkSizes.Count);
                foreach(int size in entry.ChunkSizes)
                {
                    bw.Write(size);
                }
            }

            // 3. Update Global Header
            fsPackage.Position = 8; // Offset of DirectoryOffset
            fsPackage.Write(BitConverter.GetBytes(directoryOffset));
        }

        private unsafe void ExtractGamePackage(string packagePath, string outputDirectory, IProgress<int>? progress, CancellationToken token)
        {
            using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            // 1. Read Header
            byte[] magic = br.ReadBytes(4);
            if (Encoding.ASCII.GetString(magic) != "GPCK")
                throw new InvalidDataException("Invalid Game Package file signature.");

            int version = br.ReadInt32();
            if (version != 2)
                throw new InvalidDataException($"Unsupported GPCK version: {version}. This tool supports Version 2.");

            long directoryOffset = br.ReadInt64();

            // 2. Jump to Directory
            fs.Position = directoryOffset;
            int fileCount = br.ReadInt32();

            var entries = new List<PackageEntry>();
            for(int i=0; i<fileCount; i++)
            {
                int pathLen = br.ReadInt32();
                byte[] pathBytes = br.ReadBytes(pathLen);
                string path = Encoding.UTF8.GetString(pathBytes);
                long offset = br.ReadInt64();
                long origSize = br.ReadInt64();

                int chunkCount = br.ReadInt32();
                var chunkSizes = new List<int>(chunkCount);
                for(int c=0; c<chunkCount; c++)
                {
                    chunkSizes.Add(br.ReadInt32());
                }

                entries.Add(new PackageEntry { Path = path, Offset = offset, OriginalSize = origSize, ChunkSizes = chunkSizes });
            }

            // 3. Extract Files
            ulong maxCompBound = GDeflateCpuApi.CompressBound(ChunkSize);
            void* pInput = NativeMemory.Alloc((nuint)maxCompBound);
            void* pOutput = NativeMemory.Alloc(ChunkSize);

            try
            {
                int current = 0;
                foreach(var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    string fullPath = Path.Combine(outputDirectory, entry.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

                    if (entry.OriginalSize == 0)
                    {
                        File.Create(fullPath).Dispose();
                    }
                    else
                    {
                        fs.Position = entry.Offset;
                        using var fsOut = new FileStream(fullPath, FileMode.Create, FileAccess.Write);

                        long remainingSize = entry.OriginalSize;

                        foreach(int cSize in entry.ChunkSizes)
                        {
                            // Calculate Uncompressed Size for this chunk
                            // Usually 64KB, unless it's the last one.
                            int uSize = (int)Math.Min(ChunkSize, remainingSize);

                            // Read Raw Compressed Data (No Inline Headers)
                            var inputSpan = new Span<byte>(pInput, cSize);
                            int dRead = fs.Read(inputSpan);
                            if (dRead != cSize) throw new EndOfStreamException($"Unexpected end of stream in {entry.Path}");

                            // Decompress
                            bool success = GDeflateCpuApi.Decompress(pOutput, (ulong)uSize, pInput, (ulong)cSize, 1);
                            if (!success) throw new Exception($"Decompression failed in {entry.Path}");

                            // Write
                            var outputSpan = new ReadOnlySpan<byte>(pOutput, uSize);
                            fsOut.Write(outputSpan);

                            remainingSize -= uSize;
                        }
                    }
                    current++;
                    progress?.Report((int)((current / (float)fileCount) * 100));
                }
            }
            finally
            {
                NativeMemory.Free(pInput);
                NativeMemory.Free(pOutput);
            }
        }

        #endregion

        private void EnsureBackend()
        {
            if (!IsCpuLibraryAvailable())
                throw new FileNotFoundException("GDeflate.dll not found.");
        }
    }

    public class PackageInfo
    {
        public string FilePath { get; set; } = "";
        public string Magic { get; set; } = "";
        public int Version { get; set; }
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public List<PackageEntryInfo> Entries { get; set; } = new();
    }

    public class PackageEntryInfo
    {
        public string Path { get; set; } = "";
        public long Offset { get; set; }
        public long OriginalSize { get; set; }
        public long CompressedSize { get; set; }
        public int ChunkCount { get; set; }
        public bool Is4KAligned { get; set; }
    }
}
