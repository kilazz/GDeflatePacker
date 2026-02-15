
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace GDeflate.Core
{
    /// <summary>
    /// GDeflate Archive v6 (AAAA Specs).
    /// Features:
    /// - Compact Metadata (24 bytes per file).
    /// - Block-Level Deduplication (Indirection via BlockTable).
    /// - Zero-Copy Metadata Access.
    /// </summary>
    public unsafe class GDeflateArchive : IDisposable
    {
        public const int Version = 6;
        public const string Magic = "GPCK";

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _basePtr;
        private readonly long _fileLength;
        private readonly FileStream _dataFileStream; // Used for Async Scatter/Gather Data Reads

        // Headers
        private readonly int _fileCount;
        private readonly int _totalBlockCount;
        
        // Offsets
        private readonly long _fileTableOffset;
        private readonly long _blockTableOffset;
        private readonly long _nameTableOffset;

        public string FilePath { get; }
        public int FileCount => _fileCount;
        
        // Encryption Key
        public byte[]? DecryptionKey { get; set; }

        // --- Compact Metadata (24 Bytes) ---
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FileEntry
        {
            public ulong PathHash;      // 8
            public uint FirstBlockIndex;// 4 (Index into Global Block Table)
            public uint BlockCount;     // 4
            public uint OriginalSize;   // 4 (Max 4GB per asset, sufficient for game assets)
            public uint Flags;          // 4
        }
        // Total: 24 Bytes

        // --- Global Block Table Entry (16 Bytes) ---
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BlockEntry
        {
            public long PhysicalOffset; // 8
            public uint CompressedSize; // 4
            public uint UncompressedSize;// 4 (Usually 65536, less for last chunk)
        }

        // --- Flags ---
        public const uint FLAG_IS_COMPRESSED = 1 << 0;
        public const uint FLAG_ENCRYPTED     = 1 << 1; 
        public const uint MASK_METHOD        = 0x1C; // Bits 2,3,4
        public const uint METHOD_GDEFLATE    = 0 << 2;
        public const uint METHOD_DEFLATE     = 1 << 2; 
        public const uint METHOD_ZSTD        = 2 << 2;

        public GDeflateArchive(string path)
        {
            FilePath = path;
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException(path);
            _fileLength = fi.Length;

            // Map Metadata only (Mapping huge data files consumes VmSize unnecessarily)
            // We use MMF for tables, FileStream for data body.
            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            
            // Keep a separate handle for Async IO
            _dataFileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);

            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePtr = ptr;

            if (_fileLength < 64) throw new InvalidDataException("File too short");

            // Header Parsing
            ReadOnlySpan<byte> magicFn = new ReadOnlySpan<byte>(_basePtr, 4);
            if (!magicFn.SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
                throw new InvalidDataException("Invalid Header");

            int ver = *(int*)(_basePtr + 4);
            if (ver != Version) throw new NotSupportedException($"Version mismatch. Archive: {ver}, Engine: {Version}");

            _fileCount = *(int*)(_basePtr + 8);
            _totalBlockCount = *(int*)(_basePtr + 12);

            _fileTableOffset = *(long*)(_basePtr + 16);
            _blockTableOffset = *(long*)(_basePtr + 24);
            _nameTableOffset = *(long*)(_basePtr + 32);
        }

        public FileEntry GetEntryByIndex(int index)
        {
            if (index < 0 || index >= _fileCount) throw new IndexOutOfRangeException();
            byte* ptr = _basePtr + _fileTableOffset + (index * sizeof(FileEntry));
            return *(FileEntry*)ptr;
        }

        public BlockEntry GetBlockEntry(uint globalBlockIndex)
        {
            if (globalBlockIndex >= _totalBlockCount) throw new IndexOutOfRangeException("Block index out of range");
            byte* ptr = _basePtr + _blockTableOffset + (globalBlockIndex * sizeof(BlockEntry));
            return *(BlockEntry*)ptr;
        }

        /// <summary>
        /// Gets the handle for raw Async I/O (Scatter/Gather).
        /// </summary>
        public SafeHandle GetFileHandle() => _dataFileStream.SafeFileHandle;

        public bool TryGetEntry(string identifier, out FileEntry entry)
        {
            ulong hash = PathHasher.Hash(identifier);
            int left = 0;
            int right = _fileCount - 1;

            // Binary Search on Compact Metadata
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                FileEntry midEntry = GetEntryByIndex(mid);

                if (midEntry.PathHash == hash)
                {
                    entry = midEntry;
                    return true;
                }
                if (midEntry.PathHash < hash) left = mid + 1;
                else right = mid - 1;
            }

            entry = default;
            return false;
        }

        public string? GetPathForHash(ulong hash)
        {
            if (_nameTableOffset == 0) return null;
            byte* ptr = _basePtr + _nameTableOffset;
            byte* end = _basePtr + _fileLength;

            for (int i = 0; i < _fileCount; i++)
            {
                if (ptr >= end) break;
                ulong entryHash = *(ulong*)ptr;
                ptr += 8;

                int length = 0;
                int shift = 0;
                byte b;
                do
                {
                    b = *ptr++;
                    length |= (b & 0x7F) << shift;
                    shift += 7;
                } while ((b & 0x80) != 0);

                if (entryHash == hash) return Encoding.UTF8.GetString(ptr, length);
                ptr += length;
            }
            return null;
        }

        public Stream OpenRead(FileEntry entry)
        {
            if (entry.OriginalSize == 0) return new MemoryStream();
            return new GDeflateStream(this, entry);
        }

        public PackageInfo GetPackageInfo()
        {
            var info = new PackageInfo
            {
                FilePath = FilePath,
                Magic = Magic,
                Version = Version,
                FileCount = _fileCount,
                TotalSize = _fileLength,
                HasDebugNames = _nameTableOffset > 0
            };

            for(int i=0; i < Math.Min(_fileCount, 2000); i++) // Limit inspection for performance
            {
                var e = GetEntryByIndex(i);
                
                // Calculate compressed size by summing blocks (since it's not in FileEntry anymore)
                long compSize = 0;
                long offset = 0;
                if (e.BlockCount > 0)
                {
                    var first = GetBlockEntry(e.FirstBlockIndex);
                    offset = first.PhysicalOffset;
                    for(uint b=0; b<e.BlockCount; b++) compSize += GetBlockEntry(e.FirstBlockIndex + b).CompressedSize;
                }

                string methodStr = "Store";
                if ((e.Flags & FLAG_IS_COMPRESSED) != 0)
                {
                    uint m = e.Flags & MASK_METHOD;
                    if (m == METHOD_GDEFLATE) methodStr = "GDeflate";
                    else if (m == METHOD_DEFLATE) methodStr = "Deflate";
                    else if (m == METHOD_ZSTD) methodStr = "Zstd";
                }
                if ((e.Flags & FLAG_ENCRYPTED) != 0) methodStr += " [Enc]";

                info.Entries.Add(new PackageEntryInfo
                {
                    Path = GetPathForHash(e.PathHash) ?? $"0x{e.PathHash:X16}",
                    PathHash = e.PathHash,
                    Offset = offset,
                    OriginalSize = e.OriginalSize,
                    CompressedSize = compSize,
                    ChunkCount = (int)e.BlockCount,
                    Method = methodStr
                });
            }
            return info;
        }

        public void Dispose()
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _view?.Dispose();
            _mmf?.Dispose();
            _dataFileStream?.Dispose();
        }
    }
}
