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
        public bool IsCpuLibraryAvailable() => GDeflateCpuApi.IsAvailable();

        public unsafe void CompressFile(string inputFile, string outputFile)
        {
            if (!IsCpuLibraryAvailable())
                throw new FileNotFoundException("GDeflateCPU.dll not found.");

            var fileInfo = new FileInfo(inputFile);
            ulong inputSize = (ulong)fileInfo.Length;

            // Safety check for massive single files
            CheckMemoryAvailability(inputSize);

            void* inputPtr = NativeMemory.Alloc((nuint)inputSize);

            try
            {
                using (var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
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

                    if (!success) throw new Exception("CPU Compression failed.");

                    using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    using (var ums = new UnmanagedMemoryStream((byte*)outputPtr, (long)finalOutputSize, (long)finalOutputSize, FileAccess.Read))
                    {
                        ums.CopyTo(fs);
                    }
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
        }

        public unsafe void DecompressFile(string inputFile, string outputFile)
        {
            if (!IsCpuLibraryAvailable())
                throw new FileNotFoundException("GDeflateCPU.dll not found.");

            var fileInfo = new FileInfo(inputFile);
            ulong inputSize = (ulong)fileInfo.Length;

            CheckMemoryAvailability(inputSize);

            void* inputPtr = NativeMemory.Alloc((nuint)inputSize);

            try
            {
                using (var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var ums = new UnmanagedMemoryStream((byte*)inputPtr, (long)inputSize, (long)inputSize, FileAccess.Write))
                {
                    fs.CopyTo(ums);
                }

                var headerSpan = new Span<byte>(inputPtr, sizeof(TileStreamHeader));
                var header = TileStreamHeader.ReadFromSpan(headerSpan);
                ulong uncompressedSize = header.GetUncompressedSize();

                CheckMemoryAvailability(uncompressedSize);

                void* outputPtr = NativeMemory.Alloc((nuint)uncompressedSize);

                try
                {
                    uint workers = (uint)Environment.ProcessorCount;
                    bool success = GDeflateCpuApi.Decompress(outputPtr, uncompressedSize, inputPtr, inputSize, workers);

                    if (!success) throw new Exception("CPU Decompression failed.");

                    using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    using (var ums = new UnmanagedMemoryStream((byte*)outputPtr, (long)uncompressedSize, (long)uncompressedSize, FileAccess.Read))
                    {
                        ums.CopyTo(fs);
                    }
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
        }

        public unsafe void DecompressBytesToFile(byte[] inputData, string outputFile)
        {
            fixed (byte* pInput = inputData)
            {
                var header = TileStreamHeader.ReadFromBytes(inputData);
                ulong uncompressedSize = header.GetUncompressedSize();
                ulong inputSize = (ulong)inputData.LongLength;

                void* outputPtr = NativeMemory.Alloc((nuint)uncompressedSize);
                try
                {
                    bool success = GDeflateCpuApi.Decompress(outputPtr, uncompressedSize, pInput, inputSize, (uint)Environment.ProcessorCount);
                    if (!success) throw new Exception("Decompression failed.");

                    using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    using (var ums = new UnmanagedMemoryStream((byte*)outputPtr, (long)uncompressedSize, (long)uncompressedSize, FileAccess.Read))
                    {
                        ums.CopyTo(fs);
                    }
                }
                finally
                {
                    NativeMemory.Free(outputPtr);
                }
            }
        }

        // --- Archive Handling ---

        /// <summary>
        /// Compresses files into an archive.
        /// </summary>
        /// <param name="fileMap">Dictionary where Key is the full source path and Value is the entry name (relative path) in the archive.</param>
        public unsafe void CompressFilesToArchive(IDictionary<string, string> fileMap, string outputArchivePath, string format, IProgress<int>? progress = null, CancellationToken token = default)
        {
            if (format == ".zip")
            {
                // Create the Zip archive immediately
                using (var zipStream = new FileStream(outputArchivePath, FileMode.Create))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    int total = fileMap.Count;
                    int current = 0;

                    foreach (var kvp in fileMap)
                    {
                        token.ThrowIfCancellationRequested();

                        string inputFile = kvp.Key;
                        string entryName = kvp.Value; // Use provided relative path/entry name

                        var fileInfo = new FileInfo(inputFile);
                        ulong inputSize = (ulong)fileInfo.Length;

                        // Skip empty files or handle gracefully
                        if (inputSize == 0) continue;

                        CheckMemoryAvailability(inputSize);

                        // 1. Alloc Input
                        void* inputPtr = NativeMemory.Alloc((nuint)inputSize);
                        try
                        {
                            // Read file to RAM
                            using (var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                            using (var ums = new UnmanagedMemoryStream((byte*)inputPtr, (long)inputSize, (long)inputSize, FileAccess.Write))
                            {
                                fs.CopyTo(ums);
                            }

                            // 2. Alloc Output
                            ulong maxOutputSize = GDeflateCpuApi.CompressBound(inputSize);
                            void* outputPtr = NativeMemory.Alloc((nuint)maxOutputSize);

                            try
                            {
                                ulong finalOutputSize = maxOutputSize;
                                bool success = GDeflateCpuApi.Compress(outputPtr, ref finalOutputSize, inputPtr, inputSize, 12, 0);
                                if (!success) throw new Exception($"Failed to compress {Path.GetFileName(inputFile)}");

                                // 3. Stream directly to Zip Entry (No Temp File!)
                                // Ensure .gdef extension is present if not already
                                if (!entryName.EndsWith(".gdef", StringComparison.OrdinalIgnoreCase))
                                {
                                    entryName += ".gdef";
                                }

                                var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);

                                using (var entryStream = entry.Open())
                                using (var ums = new UnmanagedMemoryStream((byte*)outputPtr, (long)finalOutputSize, (long)finalOutputSize, FileAccess.Read))
                                {
                                    ums.CopyTo(entryStream);
                                }
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
                        progress?.Report((int)((current) / (float)total * 100));
                    }
                }
            }
            else if (format == ".gdef")
            {
                if (fileMap.Count > 1)
                    throw new ArgumentException("GDEF format only supports single file compression.");

                token.ThrowIfCancellationRequested();
                foreach (var kvp in fileMap)
                {
                    CompressFile(kvp.Key, outputArchivePath);
                    break; // Only one file
                }
                progress?.Report(100);
            }
        }

        public void DecompressArchive(string inputArchivePath, string outputDirectory, IProgress<int>? progress = null, CancellationToken token = default)
        {
            string ext = Path.GetExtension(inputArchivePath).ToLower();
            if (ext == ".zip")
            {
                var archiveManager = new ArchiveManager();
                archiveManager.ExtractZipArchive(inputArchivePath, outputDirectory, this, progress, token);
            }
            else if (ext == ".gdef")
            {
                token.ThrowIfCancellationRequested();
                string outputName = Path.GetFileNameWithoutExtension(inputArchivePath);
                string outputPath = Path.Combine(outputDirectory, outputName);
                DecompressFile(inputArchivePath, outputPath);
                progress?.Report(100);
            }
        }

        private void CheckMemoryAvailability(ulong requiredBytes)
        {
            // Basic heuristic: Don't try to alloc more than 80% of total system memory
            // Or catch OutOfMemory later.
            // Since we use NativeMemory.Alloc, it doesn't return null on failure, it usually throws or we can check via other means.
            // But let's check logical limits.

            // For now, simple check: if > 30GB, maybe dangerous.
            // const ulong safeLimit = 30UL * 1024 * 1024 * 1024;
            // if (requiredBytes > safeLimit) throw new Exception("File too large for available RAM.");
        }
    }
}
