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
    /// Implements Chunked Streaming compression to support massive files (larger than RAM)
    /// and DirectStorage-friendly 64KB block alignment.
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

        #endregion

        #region Chunked Operations (Streaming)

        /// <summary>
        /// Compresses a stream in 64KB chunks.
        /// Returns the total bytes written to the output stream.
        /// </summary>
        private unsafe long StreamCompress(Stream fsIn, Stream fsOut, ulong inputSize, CancellationToken token)
        {
            void* pInput = null;
            void* pOutput = null;
            long totalWritten = 0;

            try
            {
                // Allocate buffers once
                pInput = NativeMemory.Alloc(ChunkSize);
                ulong bound = GDeflateCpuApi.CompressBound(ChunkSize);
                pOutput = NativeMemory.Alloc((nuint)bound);

                using var umsIn = new UnmanagedMemoryStream((byte*)pInput, ChunkSize, ChunkSize, FileAccess.Write);
                using var umsOut = new UnmanagedMemoryStream((byte*)pOutput, (long)bound, (long)bound, FileAccess.Read);

                // Use a single IO buffer large enough for both input reading and output writing (worst case)
                byte[] ioBuffer = new byte[Math.Max(ChunkSize, (int)bound)];
                byte[] sizeHeader = new byte[8]; // [CompSize(4)][OrigSize(4)]

                long remaining = (long)inputSize;

                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();

                    // 1. Read Chunk
                    int bytesToRead = (int)Math.Min(ChunkSize, remaining);

                    // Reset Stream Pointers
                    umsIn.Seek(0, SeekOrigin.Begin);

                    // Read from File -> Managed Buffer -> Unmanaged Buffer
                    int bytesRead = fsIn.Read(ioBuffer, 0, bytesToRead);
                    if (bytesRead == 0) break;
                    umsIn.Write(ioBuffer, 0, bytesRead);

                    // 2. Compress Chunk
                    ulong compressedSize = bound;
                    bool success = GDeflateCpuApi.Compress(pOutput, ref compressedSize, pInput, (ulong)bytesRead, 12, 0);

                    if (!success) throw new Exception("GDeflate chunk compression failed.");

                    // 3. Write Chunk Header [CompressedSize (int) | UncompressedSize (int)]
                    // This allows the decompressor to know exactly how much to read and alloc.
                    BitConverter.TryWriteBytes(new Span<byte>(sizeHeader, 0, 4), (int)compressedSize);
                    BitConverter.TryWriteBytes(new Span<byte>(sizeHeader, 4, 4), bytesRead);
                    fsOut.Write(sizeHeader);
                    totalWritten += 8;

                    // 4. Write Compressed Data
                    umsOut.Seek(0, SeekOrigin.Begin);
                    umsOut.Read(ioBuffer, 0, (int)compressedSize);
                    fsOut.Write(ioBuffer, 0, (int)compressedSize);

                    totalWritten += (long)compressedSize;
                    remaining -= bytesRead;
                }
            }
            finally
            {
                if (pInput != null) NativeMemory.Free(pInput);
                if (pOutput != null) NativeMemory.Free(pOutput);
            }

            return totalWritten;
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

            // Stream Body
            StreamCompress(fsIn, fsOut, (ulong)fsIn.Length, token);
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
                // Fallback to Legacy Single Pass if "GCHK" is missing (assumes raw size prefix)
                fsIn.Seek(0, SeekOrigin.Begin);
                DecompressLegacy(fsIn, outputFile, token);
                return;
            }

            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write);

            void* pInput = null;
            void* pOutput = null;

            try
            {
                // Allocate max buffers
                ulong maxCompBound = GDeflateCpuApi.CompressBound(ChunkSize);
                pInput = NativeMemory.Alloc((nuint)maxCompBound);
                pOutput = NativeMemory.Alloc(ChunkSize);

                using var umsIn = new UnmanagedMemoryStream((byte*)pInput, (long)maxCompBound, (long)maxCompBound, FileAccess.Write);
                using var umsOut = new UnmanagedMemoryStream((byte*)pOutput, ChunkSize, ChunkSize, FileAccess.Read);

                byte[] headerBuffer = new byte[8];
                byte[] ioBuffer = new byte[(int)maxCompBound]; // Reusable managed buffer

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
                    int dataRead = fsIn.Read(ioBuffer, 0, compSize);
                    if (dataRead != compSize) throw new EndOfStreamException("Truncated chunk body.");

                    // Move to Native
                    umsIn.Seek(0, SeekOrigin.Begin);
                    umsIn.Write(ioBuffer, 0, compSize);

                    // Decompress
                    bool success = GDeflateCpuApi.Decompress(pOutput, (ulong)origSize, pInput, (ulong)compSize, 1);
                    if (!success) throw new Exception("Chunk decompression failed.");

                    // Write to Disk
                    umsOut.Seek(0, SeekOrigin.Begin);
                    umsOut.Read(ioBuffer, 0, origSize);
                    fsOut.Write(ioBuffer, 0, origSize);

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

        /// <summary>
        /// Fallback for old .gdef files that were single-pass compressed.
        /// </summary>
        private unsafe void DecompressLegacy(FileStream fsIn, string outputFile, CancellationToken token)
        {
            byte[] sizeBuffer = new byte[8];
            fsIn.ReadExactly(sizeBuffer);
            ulong originalSize = BitConverter.ToUInt64(sizeBuffer, 0);

            long compressedPayloadSize = fsIn.Length - 8;

            // Prepare Output
            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write);

            // This is the memory-hungry path, kept only for legacy support
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

        #region Game Package Operations (DirectStorage Optimized + Streaming)

        private struct PackageEntry
        {
            public string Path;
            public long Offset;
            public long CompressedSize;
            public long OriginalSize;
        }

        private unsafe void CompressGamePackage(IDictionary<string, string> fileMap, string outputPath, IProgress<int>? progress, CancellationToken token)
        {
            // 1. Calculate Initial Header Size
            long estimatedHeaderSize = 12; // Magic+Ver+Count
            foreach(var kvp in fileMap)
            {
                estimatedHeaderSize += 4 + Encoding.UTF8.GetByteCount(kvp.Value) + 24;
            }

            long firstFileOffset = (estimatedHeaderSize + 4095) & ~4095;

            using var fsPackage = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite);

            // Fill Header Space with zeros for now
            fsPackage.SetLength(firstFileOffset);
            fsPackage.Position = firstFileOffset;

            var entries = new List<PackageEntry>();
            int current = 0;

            foreach (var kvp in fileMap)
            {
                token.ThrowIfCancellationRequested();

                string inputFile = kvp.Key;
                string relativePath = kvp.Value;
                var fileInfo = new FileInfo(inputFile);
                ulong inputSize = (ulong)fileInfo.Length;

                long currentOffset = fsPackage.Position;

                // Alignment
                if (currentOffset % 4096 != 0)
                {
                    long padding = 4096 - (currentOffset % 4096);
                    fsPackage.Write(new byte[padding]);
                    currentOffset = fsPackage.Position;
                }

                long compressedSize = 0;

                if (inputSize > 0)
                {
                    using (var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        // Use the Streaming Compressor to write directly to the package
                        // This writes: [BlockSize][OrigSize][Data]...
                        compressedSize = StreamCompress(fsIn, fsPackage, inputSize, token);
                    }
                }

                entries.Add(new PackageEntry
                {
                    Path = relativePath,
                    Offset = currentOffset,
                    CompressedSize = compressedSize, // Total size of all chunks + headers
                    OriginalSize = (long)inputSize
                });

                current++;
                progress?.Report((int)((current / (float)fileMap.Count) * 100));

                // Post-file padding
                long endPos = fsPackage.Position;
                long pad = 4096 - (endPos % 4096);
                if (pad != 4096) fsPackage.Write(new byte[pad]);
            }

            // 2. Write Real Header
            fsPackage.Position = 0;
            using var bw = new BinaryWriter(fsPackage, Encoding.UTF8, true);

            bw.Write(Encoding.ASCII.GetBytes("GPCK"));
            bw.Write((int)1);
            bw.Write(entries.Count);

            foreach(var entry in entries)
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(entry.Path);
                bw.Write(pathBytes.Length);
                bw.Write(pathBytes);
                bw.Write(entry.Offset);
                bw.Write(entry.CompressedSize);
                bw.Write(entry.OriginalSize);
            }
        }

        private unsafe void ExtractGamePackage(string packagePath, string outputDirectory, IProgress<int>? progress, CancellationToken token)
        {
            using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            byte[] magic = br.ReadBytes(4);
            if (Encoding.ASCII.GetString(magic) != "GPCK")
                throw new InvalidDataException("Invalid Game Package file signature.");

            br.ReadInt32(); // Version
            int fileCount = br.ReadInt32();
            var entries = new List<PackageEntry>();

            for(int i=0; i<fileCount; i++)
            {
                int pathLen = br.ReadInt32();
                byte[] pathBytes = br.ReadBytes(pathLen);
                string path = Encoding.UTF8.GetString(pathBytes);
                long offset = br.ReadInt64();
                long compSize = br.ReadInt64();
                long origSize = br.ReadInt64();

                entries.Add(new PackageEntry { Path = path, Offset = offset, CompressedSize = compSize, OriginalSize = origSize });
            }

            // Reuse buffers for extraction
            ulong maxCompBound = GDeflateCpuApi.CompressBound(ChunkSize);
            void* pInput = NativeMemory.Alloc((nuint)maxCompBound);
            void* pOutput = NativeMemory.Alloc(ChunkSize);
            byte[] ioBuffer = new byte[(int)maxCompBound];
            byte[] headerBuffer = new byte[8];

            try
            {
                using var umsIn = new UnmanagedMemoryStream((byte*)pInput, (long)maxCompBound, (long)maxCompBound, FileAccess.Write);

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

                        long bytesProcessed = 0;
                        while(bytesProcessed < entry.CompressedSize)
                        {
                            // Read Chunk Header
                            int hRead = fs.Read(headerBuffer, 0, 8);
                            if (hRead < 8) break;
                            bytesProcessed += 8;

                            int cSize = BitConverter.ToInt32(headerBuffer, 0);
                            int oSize = BitConverter.ToInt32(headerBuffer, 4);

                            // Read Data
                            int dRead = fs.Read(ioBuffer, 0, cSize);
                            bytesProcessed += dRead;

                            // Decompress
                            umsIn.Seek(0, SeekOrigin.Begin);
                            umsIn.Write(ioBuffer, 0, dRead);

                            bool success = GDeflateCpuApi.Decompress(pOutput, (ulong)oSize, pInput, (ulong)cSize, 1);
                            if(!success) throw new Exception($"Decompression failed in {entry.Path}");

                            // Write
                            using(var umsOut = new UnmanagedMemoryStream((byte*)pOutput, oSize, oSize, FileAccess.Read))
                            {
                                umsOut.CopyTo(fsOut);
                            }
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
}
