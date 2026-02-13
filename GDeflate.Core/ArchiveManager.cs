using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;

namespace GDeflate.Core
{
    public class ArchiveManager
    {
        // Compression logic moved to GDeflateProcessor to utilize Unsafe NativeMemory pointers directly without temp files.

        public unsafe void ExtractZipArchive(string archivePath, string outputDirectory, GDeflateProcessor processor, IProgress<int>? progress = null, CancellationToken token = default)
        {
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                bool hasGdefFiles = false;
                int totalEntries = archive.Entries.Count;
                int processed = 0;

                foreach (var entry in archive.Entries)
                {
                    // Check cancellation between files
                    token.ThrowIfCancellationRequested();

                    string outputPath = Path.Combine(outputDirectory, entry.FullName);
                    string? dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    if (entry.Name.EndsWith(".gdef", StringComparison.OrdinalIgnoreCase))
                    {
                        hasGdefFiles = true;

                        long compressedSize = entry.Length;
                        void* inputPtr = NativeMemory.Alloc((nuint)compressedSize);

                        try
                        {
                            using (var entryStream = entry.Open())
                            using (var ums = new UnmanagedMemoryStream((byte*)inputPtr, compressedSize, compressedSize, FileAccess.Write))
                            {
                                entryStream.CopyTo(ums);
                            }

                            string finalPath = Path.ChangeExtension(outputPath, null);
                            var headerSpan = new Span<byte>(inputPtr, sizeof(TileStreamHeader));
                            var header = TileStreamHeader.ReadFromSpan(headerSpan);
                            ulong uncompressedSize = header.GetUncompressedSize();

                            void* outputPtr = NativeMemory.Alloc((nuint)uncompressedSize);
                            try
                            {
                                bool success = GDeflateCpuApi.Decompress(outputPtr, uncompressedSize, inputPtr, (ulong)compressedSize, (uint)Environment.ProcessorCount);
                                if (!success) throw new Exception("Decompression failed for " + entry.Name);

                                using (var fs = new FileStream(finalPath, FileMode.Create, FileAccess.Write))
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
                    else
                    {
                        entry.ExtractToFile(outputPath, true);
                    }

                    processed++;
                    progress?.Report((int)((processed / (float)totalEntries) * 100));
                }

                if (!hasGdefFiles)
                {
                    throw new InvalidDataException("The selected .zip archive does not contain any .gdef files.");
                }
            }
        }
    }
}
