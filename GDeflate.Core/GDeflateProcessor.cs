using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace GDeflate.Core
{
    public class GDeflateProcessor
    {
        // --- Constants ---

        // 64MB is the "Sweet Spot" for DirectStorage.
        // Large enough for GPU parallelism, small enough to keep RAM usage low.
        private const int STREAM_BLOCK_SIZE = 64 * 1024 * 1024;

        // Magic Header for our custom .gdef v2 streaming format
        // 0xFB04 = ID, 0x0002 = Version 2 (Streaming)
        private const uint GDEF_MAGIC_V2 = 0x0002FB04;

        /// <summary>
        /// Checks if the native GDeflateCPU.dll library is loaded and available.
        /// </summary>
        public bool IsCpuLibraryAvailable() => GDeflateCpuApi.IsAvailable();

        // --- STREAMING COMPRESSION (O(1) Memory Usage) ---

        /// <summary>
        /// Compresses a single file using chunk-based streaming.
        /// Memory usage is constant (approx. 70MB) regardless of file size.
        /// </summary>
        public unsafe void CompressFileStreaming(string inputFile, string outputFile, IProgress<int>? progress = null, CancellationToken token = default)
        {
            if (!IsCpuLibraryAvailable())
                throw new FileNotFoundException("GDeflateCPU.dll not found.");

            // Open input with SequentialScan for SSD optimization
            using var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);

            long totalLength = fsIn.Length;
            long processedBytes = 0;

            // 1. Write File Header
            // [Magic: 4 bytes] + [OriginalSize: 8 bytes]
            fsOut.Write(BitConverter.GetBytes(GDEF_MAGIC_V2));
            fsOut.Write(BitConverter.GetBytes((ulong)totalLength));

            // 2. Allocate Native Memory (Once for the whole operation)
            // Input buffer: 64 MB
            nuint inputBufferSize = STREAM_BLOCK_SIZE;
            // Output buffer: Calculate max bound (usually slightly larger than input)
            nuint outputBufferSize = (nuint)GDeflateCpuApi.CompressBound(STREAM_BLOCK_SIZE);

            void* pInput = NativeMemory.Alloc(inputBufferSize);
            void* pOutput = NativeMemory.Alloc(outputBufferSize);

            try
            {
                byte[] metadataBuffer = new byte[8]; // To write block sizes

                while (processedBytes < totalLength)
                {
                    token.ThrowIfCancellationRequested();

                    // 3. Read Chunk from Disk directly into Native Memory
                    // We use Span to write directly to the pointer
                    Span<byte> inputSpan = new Span<byte>(pInput, (int)inputBufferSize);
                    int bytesRead = fsIn.Read(inputSpan);

                    if (bytesRead == 0) break;

                    // 4. Compress the Chunk
                    ulong currentOutputSize = outputBufferSize;

                    // Level 12 is standard high compression for GDeflate
                    bool success = GDeflateCpuApi.Compress(pOutput, ref currentOutputSize, pInput, (ulong)bytesRead, 12, 0);

                    if (!success) throw new Exception("GDeflate compression failed on stream block.");

                    // 5. Write Block Metadata
                    // Format: [CompressedSize (4 bytes)] + [UncompressedSize (4 bytes)]
                    // We need UncompressedSize because the last block might be smaller than 64MB.
                    BitConverter.TryWriteBytes(metadataBuffer, (uint)currentOutputSize);
                    fsOut.Write(metadataBuffer, 0, 4);

                    BitConverter.TryWriteBytes(metadataBuffer, (uint)bytesRead);
                    fsOut.Write(metadataBuffer, 4, 4);

                    // 6. Write Compressed Data to Disk
                    ReadOnlySpan<byte> outputSpan = new ReadOnlySpan<byte>(pOutput, (int)currentOutputSize);
                    fsOut.Write(outputSpan);

                    // 7. Progress Report
                    processedBytes += bytesRead;
                    if (totalLength > 0)
                    {
                        progress?.Report((int)((double)processedBytes / totalLength * 100));
                    }
                }
            }
            finally
            {
                // Always free native memory to avoid leaks
                NativeMemory.Free(pInput);
                NativeMemory.Free(pOutput);
            }
        }

        // --- STREAMING DECOMPRESSION (O(1) Memory Usage) ---

        /// <summary>
        /// Decompresses a .gdef v2 file using chunk-based streaming.
        /// </summary>
        public unsafe void DecompressFileStreaming(string inputFile, string outputFile, IProgress<int>? progress = null, CancellationToken token = default)
        {
            if (!IsCpuLibraryAvailable())
                throw new FileNotFoundException("GDeflateCPU.dll not found.");

            using var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);

            // 1. Validate Header
            byte[] headerBuffer = new byte[12];
            int headerRead = fsIn.Read(headerBuffer, 0, 12);
            if (headerRead != 12) throw new InvalidDataException("Invalid file header.");

            uint magic = BitConverter.ToUInt32(headerBuffer, 0);
            if (magic != GDEF_MAGIC_V2)
                throw new InvalidDataException($"Unknown or incompatible GDeflate format. Expected V2 (0x{GDEF_MAGIC_V2:X}), got 0x{magic:X}.");

            ulong totalOriginalSize = BitConverter.ToUInt64(headerBuffer, 4);
            long totalWritten = 0;

            // 2. Allocate Native Memory
            // We need a buffer large enough for the compressed input (max bound)
            // And a buffer for the uncompressed output (fixed 64MB max)
            nuint maxBlockSize = STREAM_BLOCK_SIZE;
            nuint compressedBufferSize = (nuint)GDeflateCpuApi.CompressBound(STREAM_BLOCK_SIZE);

            void* pInput = NativeMemory.Alloc(compressedBufferSize);
            void* pOutput = NativeMemory.Alloc(maxBlockSize);

            byte[] metaBuffer = new byte[8];

            try
            {
                while (fsIn.Position < fsIn.Length)
                {
                    token.ThrowIfCancellationRequested();

                    // 3. Read Block Metadata: [CompSize(4)][UncompSize(4)]
                    int metaRead = fsIn.Read(metaBuffer, 0, 8);
                    if (metaRead == 0) break; // EOF
                    if (metaRead != 8) throw new EndOfStreamException("Unexpected end of stream while reading block header.");

                    uint compSize = BitConverter.ToUInt32(metaBuffer, 0);
                    uint uncompSize = BitConverter.ToUInt32(metaBuffer, 4);

                    // Sanity check to prevent buffer overflows if file is corrupted
                    if (compSize > compressedBufferSize)
                        throw new InvalidDataException($"Compressed block size ({compSize}) exceeds buffer limit.");
                    if (uncompSize > maxBlockSize)
                        throw new InvalidDataException($"Uncompressed block size ({uncompSize}) exceeds limit.");

                    // 4. Read Compressed Data
                    Span<byte> inputSpan = new Span<byte>(pInput, (int)compSize);
                    int bytesRead = fsIn.Read(inputSpan);
                    if (bytesRead != compSize) throw new EndOfStreamException("Incomplete compressed block data.");

                    // 5. Decompress
                    bool success = GDeflateCpuApi.Decompress(pOutput, uncompSize, pInput, compSize, (uint)Environment.ProcessorCount);
                    if (!success) throw new Exception("GDeflate decompression failed (CRC mismatch or corrupt data).");

                    // 6. Write Uncompressed Data
                    ReadOnlySpan<byte> outputSpan = new ReadOnlySpan<byte>(pOutput, (int)uncompSize);
                    fsOut.Write(outputSpan);

                    totalWritten += uncompSize;
                    if (totalOriginalSize > 0)
                    {
                        progress?.Report((int)((double)totalWritten / totalOriginalSize * 100));
                    }
                }
            }
            finally
            {
                NativeMemory.Free(pInput);
                NativeMemory.Free(pOutput);
            }
        }

        // --- ARCHIVE HANDLING (Legacy/Zip + New Streaming) ---

        /// <summary>
        /// Facade method to handle both .gdef (Single Streaming) and .zip (Multiple Files).
        /// </summary>
        public void CompressFilesToArchive(IDictionary<string, string> fileMap, string outputArchivePath, string format, IProgress<int>? progress = null, CancellationToken token = default)
        {
            if (format.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // ZIP mode currently uses the legacy "Load to RAM" approach because standard ZIP
                // doesn't support GDeflate chunking natively easily.
                // For a "Master Level" solution, you would eventually implement a custom container here too.
                CompressToZipLegacy(fileMap, outputArchivePath, progress, token);
            }
            else if (format.Equals(".gdef", StringComparison.OrdinalIgnoreCase))
            {
                // Single File Streaming Mode (High Performance)
                if (fileMap.Count != 1)
                    throw new ArgumentException("GDEF format only supports single file compression in this version.");

                foreach (var kvp in fileMap)
                {
                    CompressFileStreaming(kvp.Key, outputArchivePath, progress, token);
                    return;
                }
            }
        }

        public void DecompressArchive(string inputArchivePath, string outputDirectory, IProgress<int>? progress = null, CancellationToken token = default)
        {
            string ext = Path.GetExtension(inputArchivePath).ToLower();

            if (ext == ".gdef")
            {
                // High Performance Streaming Decompression
                string outputName = Path.GetFileNameWithoutExtension(inputArchivePath);
                string outputPath = Path.Combine(outputDirectory, outputName);
                DecompressFileStreaming(inputArchivePath, outputPath, progress, token);
                progress?.Report(100);
            }
            else if (ext == ".zip")
            {
                // Legacy ZIP Extraction
                ExtractZipLegacy(inputArchivePath, outputDirectory, progress, token);
            }
            else
            {
                throw new NotSupportedException($"Format {ext} is not supported.");
            }
        }

        // --- LEGACY METHODS (ZIP Support) ---
        // Kept for compatibility, but marked as legacy compared to the streaming methods above.

        private unsafe void CompressToZipLegacy(IDictionary<string, string> fileMap, string outputArchivePath, IProgress<int>? progress, CancellationToken token)
        {
            using var zipStream = new FileStream(outputArchivePath, FileMode.Create);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            int total = fileMap.Count;
            int current = 0;

            foreach (var kvp in fileMap)
            {
                token.ThrowIfCancellationRequested();
                string inputFile = kvp.Key;
                string entryName = kvp.Value;

                var fileInfo = new FileInfo(inputFile);
                ulong inputSize = (ulong)fileInfo.Length;

                if (inputSize == 0) continue;

                // NOTE: This legacy method loads the whole file to RAM.
                // For production, this should also be converted to streams,
                // but ZIP entries are harder to seek.
                void* inputPtr = NativeMemory.Alloc((nuint)inputSize);
                try
                {
                    using (var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read))
                    using (var ums = new UnmanagedMemoryStream((byte*)inputPtr, (long)inputSize, (long)inputSize, FileAccess.Write))
                    {
                        fs.CopyTo(ums);
                    }

                    ulong maxOutputSize = GDeflateCpuApi.CompressBound(inputSize);
                    void* outputPtr = NativeMemory.Alloc((nuint)maxOutputSize);

                    try
                    {
                        ulong finalOutputSize = maxOutputSize;
                        bool success = GDeflateCpuApi.Compress(outputPtr, ref finalOutputSize, inputPtr, inputSize, 12, 0);
                        if (!success) throw new Exception($"Failed to compress {entryName}");

                        if (!entryName.EndsWith(".gdef")) entryName += ".gdef";
                        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

                        using var entryStream = entry.Open();
                        using var ums = new UnmanagedMemoryStream((byte*)outputPtr, (long)finalOutputSize, (long)finalOutputSize, FileAccess.Read);
                        ums.CopyTo(entryStream);
                    }
                    finally
                    {
                        NativeMemory.Free(outputPtr);
                    }
                }
                finally
                {
                    NativeMemory.Free(inputPtr);
                }

                current++;
                progress?.Report((int)((current / (float)total) * 100));
            }
        }

        private unsafe void ExtractZipLegacy(string archivePath, string outputDirectory, IProgress<int>? progress, CancellationToken token)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            int total = archive.Entries.Count;
            int current = 0;

            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string outputPath = Path.Combine(outputDirectory, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

                if (entry.Name.EndsWith(".gdef", StringComparison.OrdinalIgnoreCase))
                {
                    // For legacy ZIPs, we assume the whole entry is one GDeflate block (non-streaming)
                    long compressedSize = entry.Length;
                    void* inputPtr = NativeMemory.Alloc((nuint)compressedSize);

                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var ums = new UnmanagedMemoryStream((byte*)inputPtr, compressedSize, compressedSize, FileAccess.Write))
                        {
                            entryStream.CopyTo(ums);
                        }

                        // Parse standard TileHeader (NOT our v2 streaming header)
                        var headerSpan = new Span<byte>(inputPtr, sizeof(TileStreamHeader));
                        var header = TileStreamHeader.ReadFromSpan(headerSpan);
                        ulong uncompressedSize = header.GetUncompressedSize();

                        void* outputPtr = NativeMemory.Alloc((nuint)uncompressedSize);
                        try
                        {
                            bool success = GDeflateCpuApi.Decompress(outputPtr, uncompressedSize, inputPtr, (ulong)compressedSize, (uint)Environment.ProcessorCount);
                            if (!success) throw new Exception("Decompression failed.");

                            string finalPath = Path.ChangeExtension(outputPath, null); // Remove .gdef
                            using var fs = new FileStream(finalPath, FileMode.Create);
                            using var ums = new UnmanagedMemoryStream((byte*)outputPtr, (long)uncompressedSize, (long)uncompressedSize, FileAccess.Read);
                            ums.CopyTo(fs);
                        }
                        finally { NativeMemory.Free(outputPtr); }
                    }
                    finally { NativeMemory.Free(inputPtr); }
                }
                else
                {
                    entry.ExtractToFile(outputPath, true);
                }
                current++;
                progress?.Report((int)((current / (float)total) * 100));
            }
        }
    }
}
