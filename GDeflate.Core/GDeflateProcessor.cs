using System;
using System.Buffers.Binary;
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
    /// Format v1: [Header & Directory] [Padding] [Data Blob (Aligned 4K)]
    /// Optimized for DirectStorage (Header-First).
    /// </summary>
    public class GDeflateProcessor
    {
        // 64KB is the standard page size for GPU decompression scenarios
        private const int ChunkSize = 65536;
        private const int CurrentVersion = 1;
        private const string MagicSignature = "GPCK";

        public bool IsCpuLibraryAvailable() => GDeflateCpuApi.IsAvailable();

        #region Public API

        /// <summary>
        /// Helper to generate a consistent file map from a directory, removing logic duplication from clients.
        /// </summary>
        public static Dictionary<string, string> BuildFileMap(string sourceDirectory)
        {
            var map = new Dictionary<string, string>();
            string rootDir = Path.GetFullPath(sourceDirectory);
            if (!rootDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
                rootDir += Path.DirectorySeparatorChar;

            // Use enumeration options for faster file system iteration
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

            foreach (var file in Directory.EnumerateFiles(rootDir, "*", options))
            {
                // Create relative path
                string relative = Path.GetRelativePath(rootDir, file);
                // Normalize to forward slashes for internal archive consistency
                map[file] = relative.Replace('\\', '/');
            }
            return map;
        }

        public void CompressFilesToArchive(IDictionary<string, string> fileMap, string outputPath, IProgress<int>? progress = null, CancellationToken token = default)
        {
            EnsureBackend();
            CompressGamePackage(fileMap, outputPath, progress, token);
        }

        public void DecompressArchive(string inputPath, string outputDirectory, IProgress<int>? progress = null, CancellationToken token = default)
        {
            EnsureBackend();
            ExtractGamePackage(inputPath, outputDirectory, progress, token);
        }

        public PackageInfo InspectPackage(string packagePath)
        {
            using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            var info = new PackageInfo
            {
                FilePath = packagePath,
                TotalSize = fs.Length
            };

            // Read Header at 0
            if (fs.Length < 4) return info;

            byte[] magic = br.ReadBytes(4);
            info.Magic = Encoding.ASCII.GetString(magic);

            if (info.Magic != MagicSignature) return info;

            info.Version = br.ReadInt32();
            info.FileCount = br.ReadInt32();
            long dataStartOffset = br.ReadInt64(); // Pointer to where data actually begins

            // Read Directory immediately following header
            for (int i = 0; i < info.FileCount; i++)
            {
                int pathLen = br.ReadInt32();
                byte[] pathBytes = br.ReadBytes(pathLen);
                string path = Encoding.UTF8.GetString(pathBytes);
                long offset = br.ReadInt64();
                long origSize = br.ReadInt64();
                long compSize = br.ReadInt64(); // Total compressed size of file

                int chunkCount = br.ReadInt32();
                // Skip reading individual chunk sizes for inspection summary
                long chunksMetaSize = chunkCount * 4;
                fs.Seek(chunksMetaSize, SeekOrigin.Current);

                bool isAligned = (offset % 4096) == 0;

                info.Entries.Add(new PackageEntryInfo
                {
                    Path = path,
                    Offset = offset,
                    OriginalSize = origSize,
                    CompressedSize = compSize,
                    Is4KAligned = isAligned,
                    ChunkCount = chunkCount
                });
            }

            return info;
        }

        #endregion

        #region Chunked Operations (Streaming)

        // Optimization: Appends to a shared List<int> to avoid allocating a new List per file.
        private unsafe void StreamCompress(Stream fsIn, Stream fsOut, ulong inputSize, byte* pInput, byte* pOutput, ulong bound, List<int> globalChunkList, CancellationToken token)
        {
            long remaining = (long)inputSize;

            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(ChunkSize, remaining);
                var inputSpan = new Span<byte>(pInput, bytesToRead);
                int bytesRead = fsIn.Read(inputSpan);

                if (bytesRead == 0) break;

                byte* pCompressDest = pOutput;
                ulong compressedSize = bound;

                bool success = GDeflateCpuApi.Compress(pCompressDest, ref compressedSize, pInput, (ulong)bytesRead, 12, 0);
                if (!success) throw new Exception("GDeflate chunk compression failed.");

                // Direct append to the flattened list (Zero Allocation for list structure)
                globalChunkList.Add((int)compressedSize);

                var outputSpan = new ReadOnlySpan<byte>(pCompressDest, (int)compressedSize);
                fsOut.Write(outputSpan);

                remaining -= bytesRead;
            }
        }

        #endregion

        #region Game Package Operations (Header-First / Reserved)

        private struct PackageEntry
        {
            public string Path;
            public long Offset;
            public long OriginalSize;
            public long CompressedSize;
            // Flattened optimization: We only store indices, not a whole List object
            public int ChunkStartIndex;
            public int ChunkCount;
        }

        private unsafe void CompressGamePackage(IDictionary<string, string> fileMap, string outputPath, IProgress<int>? progress, CancellationToken token)
        {
            // 1. Estimate Header Size to reserve space at the beginning
            long estimatedHeaderSize = 16 + 8; // Magic(4)+Ver(4)+Count(4)+DataStart(8) + Safety
            foreach (var kvp in fileMap)
            {
                // Per file: PathLen(4) + Path(N) + Offset(8) + Orig(8) + Comp(8) + ChunkCount(4) + ChunkArray(Cnt*4)
                // Normalize path length calculation to UTF8
                long pathBytes = Encoding.UTF8.GetByteCount(kvp.Value.Replace('\\', '/'));
                long fileSize = new FileInfo(kvp.Key).Length;
                long maxChunks = (fileSize + ChunkSize - 1) / ChunkSize;
                if (fileSize == 0) maxChunks = 0;

                estimatedHeaderSize += 4 + pathBytes + 8 + 8 + 8 + 4 + (maxChunks * 4);
            }

            // Align header reservation to 4KB (DirectStorage requirement for Data Start)
            long dataStartOffset = (estimatedHeaderSize + 4095) & ~4095;

            var entries = new List<PackageEntry>(fileMap.Count);

            // Optimization: Single large list for all chunks in the entire archive to reduce GC pressure
            var globalChunkList = new List<int>(fileMap.Count * 4);

            // Alloc Buffers
            ulong bound = GDeflateCpuApi.CompressBound(ChunkSize);
            void* pInput = NativeMemory.Alloc(ChunkSize);
            void* pOutput = NativeMemory.Alloc((nuint)bound);

            try
            {
                using (var fsFinal = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                using (var bwFinal = new BinaryWriter(fsFinal, Encoding.UTF8))
                {
                    // 2. Jump to Data Start (Reserve Header Space)
                    fsFinal.Position = dataStartOffset;

                    int current = 0;

                    // 3. Write Data
                    foreach (var kvp in fileMap)
                    {
                        token.ThrowIfCancellationRequested();

                        // Ensure 4K alignment for every file within the data block
                        long currentPos = fsFinal.Position;
                        long padding = (4096 - (currentPos % 4096)) % 4096;
                        if (padding > 0) fsFinal.Write(new byte[padding]);

                        long fileStartOffset = fsFinal.Position;
                        string inputFile = kvp.Key;
                        // Normalize path to forward slashes for archive portability
                        string relativePath = kvp.Value.Replace('\\', '/');

                        long originalSize = new FileInfo(inputFile).Length;

                        int chunkStartIndex = globalChunkList.Count;
                        long totalCompSize = 0;

                        if (originalSize > 0)
                        {
                            using (var fsIn = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                // Pass the global list directly
                                StreamCompress(fsIn, fsFinal, (ulong)originalSize, (byte*)pInput, (byte*)pOutput, bound, globalChunkList, token);
                            }

                            // Calculate compressed size from the added chunks
                            int chunkCount = globalChunkList.Count - chunkStartIndex;
                            for (int k = 0; k < chunkCount; k++)
                                totalCompSize += globalChunkList[chunkStartIndex + k];
                        }

                        entries.Add(new PackageEntry
                        {
                            Path = relativePath,
                            Offset = fileStartOffset,
                            OriginalSize = originalSize,
                            CompressedSize = totalCompSize,
                            ChunkStartIndex = chunkStartIndex,
                            ChunkCount = globalChunkList.Count - chunkStartIndex
                        });

                        current++;
                        progress?.Report((int)((current / (float)fileMap.Count) * 100));
                    }

                    // 4. Rewind and Write Header
                    fsFinal.Position = 0;

                    // Header: [Magic] [Version] [Count] [DataStartOffset]
                    bwFinal.Write(Encoding.ASCII.GetBytes(MagicSignature));
                    bwFinal.Write(CurrentVersion);
                    bwFinal.Write(entries.Count);
                    bwFinal.Write(dataStartOffset);

                    // Directory
                    foreach (var entry in entries)
                    {
                        byte[] pathBytes = Encoding.UTF8.GetBytes(entry.Path);
                        bwFinal.Write(pathBytes.Length);
                        bwFinal.Write(pathBytes);
                        bwFinal.Write(entry.Offset);
                        bwFinal.Write(entry.OriginalSize);
                        bwFinal.Write(entry.CompressedSize);
                        bwFinal.Write(entry.ChunkCount);

                        // Write chunks from the global list
                        for (int i = 0; i < entry.ChunkCount; i++)
                        {
                            bwFinal.Write(globalChunkList[entry.ChunkStartIndex + i]);
                        }
                    }

                    // 5. Safety Check
                    if (fsFinal.Position > dataStartOffset)
                    {
                        throw new Exception("Header estimation failed. Metadata exceeded reserved space. Increase safety margin.");
                    }

                    // Pad the remainder of the header with zeros to reach dataStartOffset
                    long remainingHeader = dataStartOffset - fsFinal.Position;
                    if (remainingHeader > 0)
                    {
                        // Write zeros in chunks to avoid massive array alloc
                        byte[] zeros = new byte[Math.Min(remainingHeader, 65536)];
                        while (remainingHeader > 0)
                        {
                            int toWrite = (int)Math.Min(remainingHeader, zeros.Length);
                            fsFinal.Write(zeros, 0, toWrite);
                            remainingHeader -= toWrite;
                        }
                    }
                }
            }
            finally
            {
                if (pInput != null) NativeMemory.Free(pInput);
                if (pOutput != null) NativeMemory.Free(pOutput);
            }
        }

        private unsafe void ExtractGamePackage(string packagePath, string outputDirectory, IProgress<int>? progress, CancellationToken token)
        {
            using var fs = new FileStream(packagePath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            // 1. Read Header
            byte[] magic = br.ReadBytes(4);
            if (Encoding.ASCII.GetString(magic) != MagicSignature)
                throw new InvalidDataException("Invalid Game Package file signature.");

            int version = br.ReadInt32();
            if (version != CurrentVersion)
                throw new InvalidDataException($"Unsupported GPCK version: {version}. Expected {CurrentVersion}.");

            int fileCount = br.ReadInt32();
            long dataStartOffset = br.ReadInt64();

            // Temporary list to hold directory info before we start extraction
            // Tuple<Entry, List<int>> maps the file metadata to its specific list of chunk sizes
            var extractionList = new List<(PackageEntry Entry, List<int> ChunkSizes)>(fileCount);

            // 2. Read Directory
            for (int i = 0; i < fileCount; i++)
            {
                int pathLen = br.ReadInt32();
                byte[] pathBytes = br.ReadBytes(pathLen);
                string path = Encoding.UTF8.GetString(pathBytes);
                long offset = br.ReadInt64();
                long origSize = br.ReadInt64();
                long compSize = br.ReadInt64();

                int chunkCount = br.ReadInt32();

                // Security: Zip Slip protection
                if (path.Contains("..") || Path.IsPathRooted(path))
                    throw new System.Security.SecurityException($"Malicious path detected: {path}");

                var chunkSizes = new List<int>(chunkCount);
                for (int c = 0; c < chunkCount; c++)
                {
                    chunkSizes.Add(br.ReadInt32());
                }

                var entry = new PackageEntry
                {
                    Path = path,
                    Offset = offset,
                    OriginalSize = origSize
                };

                extractionList.Add((entry, chunkSizes));
            }

            // 3. Extract Files
            ulong maxCompBound = GDeflateCpuApi.CompressBound(ChunkSize);
            void* pInput = NativeMemory.Alloc((nuint)maxCompBound);
            void* pOutput = NativeMemory.Alloc(ChunkSize);

            try
            {
                int current = 0;
                // Iterate through the prepared list
                foreach(var item in extractionList)
                {
                    var entry = item.Entry;
                    var chunkSizes = item.ChunkSizes;

                    token.ThrowIfCancellationRequested();

                    // Normalize path separators to OS default
                    string localPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                    string fullPath = Path.Combine(outputDirectory, localPath);

                    // Final Security check
                    if (!Path.GetFullPath(fullPath).StartsWith(Path.GetFullPath(outputDirectory), StringComparison.Ordinal))
                        throw new System.Security.SecurityException($"Path traversal attempt: {entry.Path}");

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

                        foreach (int cSize in chunkSizes)
                        {
                            int uSize = (int)Math.Min(ChunkSize, remainingSize);

                            var inputSpan = new Span<byte>(pInput, cSize);
                            int dRead = fs.Read(inputSpan);
                            if (dRead != cSize) throw new EndOfStreamException($"Unexpected end of stream in {entry.Path}");

                            bool success = GDeflateCpuApi.Decompress(pOutput, (ulong)uSize, pInput, (ulong)cSize, 1);
                            if (!success) throw new Exception($"Decompression failed in {entry.Path}");

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
