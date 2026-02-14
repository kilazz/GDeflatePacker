using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace GDeflate.Core
{
    /// <summary>
    /// Standard GDeflate Processor.
    /// Implements single-pass compression to produce a spec-compliant GDeflate bitstream.
    /// </summary>
    public class GDeflateProcessor
    {
        /// <summary>
        /// Checks if the native GDeflate.dll library is loaded and available.
        /// </summary>
        public bool IsCpuLibraryAvailable() => GDeflateCpuApi.IsAvailable();

        #region Public API

        public void CompressFilesToArchive(IDictionary<string, string> fileMap, string outputPath, string format, IProgress<int>? progress = null, CancellationToken token = default)
        {
            if (format.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                CompressZipArchive(fileMap, outputPath, progress, token);
            }
            else if (format.Equals(".gdef", StringComparison.OrdinalIgnoreCase))
            {
                if (fileMap.Count != 1)
                    throw new ArgumentException("GDEF format only supports single file compression.");

                foreach (var kvp in fileMap)
                {
                    CompressFileSinglePass(kvp.Key, outputPath, progress, token);
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
            string ext = Path.GetExtension(inputPath).ToLower();

            if (ext == ".gdef")
            {
                string outputName = Path.GetFileNameWithoutExtension(inputPath);
                string fullOutputPath = Path.Combine(outputDirectory, outputName);
                DecompressFileSinglePass(inputPath, fullOutputPath, progress, token);
                progress?.Report(100);
            }
            else if (ext == ".zip")
            {
                ExtractZipArchive(inputPath, outputDirectory, progress, token);
            }
            else
            {
                throw new NotSupportedException($"Format {ext} is not supported.");
            }
        }

        #endregion

        #region Single Pass GDeflate Operations (Standard Bitstream)

        /// <summary>
        /// Compresses a file into a raw GDeflate bitstream prefixed with the original 64-bit size.
        /// The size prefix is required because the GDeflateDecompress API requires the output buffer size to be known.
        /// </summary>
        private unsafe void CompressFileSinglePass(string inputFile, string outputFile, IProgress<int>? progress, CancellationToken token)
        {
            EnsureBackend();

            var fileInfo = new FileInfo(inputFile);
            ulong inputSize = (ulong)fileInfo.Length;

            // Handle empty files gracefully
            if (inputSize == 0)
            {
                using var fsOutEmpty = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                fsOutEmpty.Write(BitConverter.GetBytes((ulong)0));
                return;
            }

            // Safety check for UnmanagedMemoryStream capacity (long.MaxValue)
            if (inputSize > long.MaxValue) throw new NotSupportedException("File is too large for the current implementation (exceeds 9EB).");

            void* pInput = null;
            void* pOutput = null;

            try
            {
                // 1. Prepare Input
                try
                {
                    pInput = NativeMemory.Alloc((nuint)inputSize);
                }
                catch (OutOfMemoryException)
                {
                    throw new OutOfMemoryException($"Failed to allocate {inputSize / 1024 / 1024} MB for input buffer. System RAM is insufficient.");
                }

                using (var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
                using (var umsIn = new UnmanagedMemoryStream((byte*)pInput, (long)inputSize, (long)inputSize, FileAccess.Write))
                {
                    fsIn.CopyTo(umsIn);
                }

                token.ThrowIfCancellationRequested();

                // 2. Prepare Output
                ulong maxOutputSize = GDeflateCpuApi.CompressBound(inputSize);

                try
                {
                    pOutput = NativeMemory.Alloc((nuint)maxOutputSize);
                }
                catch (OutOfMemoryException)
                {
                     throw new OutOfMemoryException($"Failed to allocate {maxOutputSize / 1024 / 1024} MB for output buffer. System RAM is insufficient.");
                }

                // 3. Compress
                // Level 12 (High), Flags 0 (None)
                ulong compressedSize = maxOutputSize;
                bool success = GDeflateCpuApi.Compress(pOutput, ref compressedSize, pInput, inputSize, 12, 0);

                if (!success)
                    throw new Exception("GDeflate native compression failed (Internal API Error).");

                // 4. Write to Disk
                // Format: [OriginalSize (8 bytes)] + [Raw GDeflate Stream]
                using (var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                {
                    fsOut.Write(BitConverter.GetBytes(inputSize));

                    using (var umsOut = new UnmanagedMemoryStream((byte*)pOutput, (long)compressedSize, (long)compressedSize, FileAccess.Read))
                    {
                        umsOut.CopyTo(fsOut);
                    }
                }
            }
            finally
            {
                if (pInput != null) NativeMemory.Free(pInput);
                if (pOutput != null) NativeMemory.Free(pOutput);
            }
        }

        private unsafe void DecompressFileSinglePass(string inputFile, string outputFile, IProgress<int>? progress, CancellationToken token)
        {
            EnsureBackend();

            using var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read);

            // 1. Read Original Size
            byte[] sizeBuffer = new byte[8];
            // FIX: Use ReadExactly to ensure we get all bytes.
            fsIn.ReadExactly(sizeBuffer);

            ulong originalSize = BitConverter.ToUInt64(sizeBuffer, 0);

            if (originalSize == 0)
            {
                File.Create(outputFile).Dispose();
                return;
            }

            long compressedPayloadSize = fsIn.Length - 8;
            if (compressedPayloadSize <= 0) throw new InvalidDataException("Invalid file structure.");

            void* pInput = null;
            void* pOutput = null;

            try
            {
                // 2. Load Compressed Data
                try
                {
                    pInput = NativeMemory.Alloc((nuint)compressedPayloadSize);
                }
                catch (OutOfMemoryException)
                {
                    throw new OutOfMemoryException($"Not enough RAM to load compressed payload ({compressedPayloadSize / 1024 / 1024} MB).");
                }

                using (var umsIn = new UnmanagedMemoryStream((byte*)pInput, compressedPayloadSize, compressedPayloadSize, FileAccess.Write))
                {
                    fsIn.CopyTo(umsIn);
                }

                token.ThrowIfCancellationRequested();

                // 3. Prepare Output
                try
                {
                    pOutput = NativeMemory.Alloc((nuint)originalSize);
                }
                catch (OutOfMemoryException)
                {
                    throw new OutOfMemoryException($"Not enough RAM to allocate output buffer ({originalSize / 1024 / 1024} MB).");
                }

                // 4. Decompress
                bool success = GDeflateCpuApi.Decompress(pOutput, originalSize, pInput, (ulong)compressedPayloadSize, (uint)Environment.ProcessorCount);

                if (!success)
                    throw new Exception("GDeflate native decompression failed. The stream might be corrupt or invalid.");

                // 5. Write Result
                using (var fsOut = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                using (var umsOut = new UnmanagedMemoryStream((byte*)pOutput, (long)originalSize, (long)originalSize, FileAccess.Read))
                {
                    umsOut.CopyTo(fsOut);
                }
            }
            finally
            {
                if (pInput != null) NativeMemory.Free(pInput);
                if (pOutput != null) NativeMemory.Free(pOutput);
            }
        }

        #endregion

        #region ZIP Archive Operations

        private unsafe void CompressZipArchive(IDictionary<string, string> fileMap, string outputArchivePath, IProgress<int>? progress, CancellationToken token)
        {
            EnsureBackend();

            using var zipStream = new FileStream(outputArchivePath, FileMode.Create);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

            int total = fileMap.Count;
            int current = 0;

            foreach (var kvp in fileMap)
            {
                token.ThrowIfCancellationRequested();
                string inputFile = kvp.Key;
                string entryName = kvp.Value;
                if (!entryName.EndsWith(".gdef")) entryName += ".gdef";

                var fileInfo = new FileInfo(inputFile);
                ulong inputSize = (ulong)fileInfo.Length;

                // Create empty entry for empty file
                if (inputSize == 0)
                {
                    var emptyEntry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    using var s = emptyEntry.Open();
                    s.Write(BitConverter.GetBytes((ulong)0));
                    continue;
                }

                void* pInput = null;
                void* pOutput = null;

                try
                {
                    // Allocate Input
                    try { pInput = NativeMemory.Alloc((nuint)inputSize); }
                    catch (OutOfMemoryException) { throw new OutOfMemoryException($"Processing {Path.GetFileName(inputFile)} failed: Not enough RAM."); }

                    using (var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read))
                    using (var ums = new UnmanagedMemoryStream((byte*)pInput, (long)inputSize, (long)inputSize, FileAccess.Write))
                    {
                        fs.CopyTo(ums);
                    }

                    ulong maxOutputSize = GDeflateCpuApi.CompressBound(inputSize);

                    // Allocate Output
                    try { pOutput = NativeMemory.Alloc((nuint)maxOutputSize); }
                    catch (OutOfMemoryException) { throw new OutOfMemoryException($"Processing {Path.GetFileName(inputFile)} failed: Not enough RAM for output buffer."); }

                    ulong compressedSize = maxOutputSize;
                    bool success = GDeflateCpuApi.Compress(pOutput, ref compressedSize, pInput, inputSize, 12, 0);
                    if (!success) throw new Exception($"Failed to compress {entryName}");

                    var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    using var entryStream = entry.Open();

                    // Write Size Header (8 bytes)
                    entryStream.Write(BitConverter.GetBytes(inputSize));

                    // Write Continuous Bitstream
                    using (var ums = new UnmanagedMemoryStream((byte*)pOutput, (long)compressedSize, (long)compressedSize, FileAccess.Read))
                    {
                        ums.CopyTo(entryStream);
                    }
                }
                finally
                {
                    if (pOutput != null) NativeMemory.Free(pOutput);
                    if (pInput != null) NativeMemory.Free(pInput);
                }

                current++;
                progress?.Report((int)((current / (float)total) * 100));
            }
        }

        private unsafe void ExtractZipArchive(string archivePath, string outputDirectory, IProgress<int>? progress, CancellationToken token)
        {
            EnsureBackend();

            using var archive = ZipFile.OpenRead(archivePath);
            int total = archive.Entries.Count;
            int current = 0;

            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string outputPath = Path.Combine(outputDirectory, entry.FullName);
                string? dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (entry.Name.EndsWith(".gdef", StringComparison.OrdinalIgnoreCase))
                {
                    using var entryStream = entry.Open();

                    // Read Header
                    byte[] sizeBuffer = new byte[8];
                    // FIX: Use ReadExactly
                    try {
                        entryStream.ReadExactly(sizeBuffer);
                    } catch (EndOfStreamException) {
                        throw new InvalidDataException($"Entry {entry.Name} is too small to be valid GDeflate.");
                    }

                    ulong uncompressedSize = BitConverter.ToUInt64(sizeBuffer, 0);

                    string finalPath = Path.ChangeExtension(outputPath, null);
                    if (uncompressedSize == 0)
                    {
                        File.Create(finalPath).Dispose();
                    }
                    else
                    {
                        // Read Payload
                        long payloadSize = entry.Length - 8;
                        if(payloadSize < 0) throw new InvalidDataException("Invalid zip entry size.");

                        void* pInput = null;
                        void* pOutput = null;

                        try
                        {
                            try { pInput = NativeMemory.Alloc((nuint)payloadSize); }
                            catch (OutOfMemoryException) { throw new OutOfMemoryException($"Extracting {entry.Name} failed: Not enough RAM."); }

                            using (var umsIn = new UnmanagedMemoryStream((byte*)pInput, payloadSize, payloadSize, FileAccess.Write))
                            {
                                entryStream.CopyTo(umsIn);
                            }

                            try { pOutput = NativeMemory.Alloc((nuint)uncompressedSize); }
                            catch (OutOfMemoryException) { throw new OutOfMemoryException($"Extracting {entry.Name} failed: Not enough RAM for output."); }

                            bool success = GDeflateCpuApi.Decompress(pOutput, uncompressedSize, pInput, (ulong)payloadSize, (uint)Environment.ProcessorCount);
                            if (!success) throw new Exception($"Decompression failed for {entry.Name}");

                            using var fsOut = new FileStream(finalPath, FileMode.Create);
                            using var umsOut = new UnmanagedMemoryStream((byte*)pOutput, (long)uncompressedSize, (long)uncompressedSize, FileAccess.Read);
                            umsOut.CopyTo(fsOut);
                        }
                        finally
                        {
                            if (pInput != null) NativeMemory.Free(pInput);
                            if (pOutput != null) NativeMemory.Free(pOutput);
                        }
                    }
                }
                else
                {
                    entry.ExtractToFile(outputPath, true);
                }

                current++;
                progress?.Report((int)((current / (float)total) * 100));
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
