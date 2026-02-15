using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GDeflate.Core
{
    /// <summary>
    /// Game Archive.
    /// </summary>
    public unsafe class GDeflateArchive : IDisposable
    {
        public const int Version = 1;
        public const string Magic = "GPCK"; 
        private const int FileEntrySize = 44; // Explicit size (16+8+4+4+4+4+4)

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly byte* _basePtr;
        private readonly long _fileLength;
        private readonly FileStream _dataFileStream;

        private readonly int _fileCount;
        private readonly int _dependencyCount;
        
        private readonly long _fileTableOffset;
        private readonly long _nameTableOffset;
        private readonly long _dependencyTableOffset;

        public string FilePath { get; }
        public int FileCount => _fileCount;
        public byte[]? DecryptionKey { get; set; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FileEntry
        {
            public Guid AssetId;            
            public long DataOffset;         
            public uint CompressedSize;     
            public uint OriginalSize;       
            public uint Flags;              
            public uint Meta1;          
            public uint Meta2;          
        }

        public enum DependencyType : uint 
        {
            HardReference = 0,
            SoftReference = 1,
            Streaming     = 2
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DependencyEntry
        {
            public Guid SourceAssetId; 
            public Guid TargetAssetId; 
            public DependencyType Type;
        }
        
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ChunkHeaderEntry
        {
            public uint CompressedSize;
            public uint OriginalSize;
        }

        public const uint FLAG_IS_COMPRESSED = 1 << 0;
        public const uint FLAG_ENCRYPTED     = 1 << 1; 
        public const uint MASK_METHOD        = 0x1C; 
        public const uint METHOD_STORE       = 0 << 2;
        public const uint METHOD_GDEFLATE    = 1 << 2; 
        public const uint METHOD_ZSTD        = 2 << 2; 
        public const uint MASK_TYPE          = 0xE0;
        public const uint TYPE_GENERIC       = 0 << 5;
        public const uint TYPE_TEXTURE       = 1 << 5;
        public const uint TYPE_GEOMETRY      = 2 << 5;
        public const uint FLAG_STREAMING     = 1 << 8; 
        public const uint MASK_ALIGNMENT     = 0xFF000000;
        public const int SHIFT_ALIGNMENT     = 24;

        public static uint GetAlignmentFromFlags(uint flags)
        {
            int power = (int)((flags & MASK_ALIGNMENT) >> SHIFT_ALIGNMENT);
            return power == 0 ? 4096 : (1u << power);
        }

        public GDeflateArchive(string path)
        {
            FilePath = path;
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException(path);
            _fileLength = fi.Length;

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _dataFileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);

            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            _basePtr = ptr;

            if (_fileLength < 64) throw new InvalidDataException("File too short");

            ReadOnlySpan<byte> magicFn = new ReadOnlySpan<byte>(_basePtr, 4);
            if (!magicFn.SequenceEqual(Encoding.ASCII.GetBytes(Magic)))
                throw new InvalidDataException("Invalid Header. Expected GPCK.");

            int ver = *(int*)(_basePtr + 4);
            if (ver != Version) throw new NotSupportedException($"Version mismatch. Archive: {ver}, Engine: {Version}");

            _fileCount = *(int*)(_basePtr + 8);
            _dependencyCount = *(int*)(_basePtr + 16);

            _fileTableOffset = *(long*)(_basePtr + 24);
            _nameTableOffset = *(long*)(_basePtr + 40);
            _dependencyTableOffset = *(long*)(_basePtr + 48);
        }

        public FileEntry GetEntryByIndex(int index)
        {
            if (index < 0 || index >= _fileCount) throw new IndexOutOfRangeException();
            byte* ptr = _basePtr + _fileTableOffset + (index * FileEntrySize);
            return *(FileEntry*)ptr;
        }

        public List<DependencyEntry> GetDependencies()
        {
            var list = new List<DependencyEntry>(_dependencyCount);
            if (_dependencyCount == 0 || _dependencyTableOffset == 0) return list;

            DependencyEntry* ptr = (DependencyEntry*)(_basePtr + _dependencyTableOffset);
            for(int i=0; i<_dependencyCount; i++) list.Add(ptr[i]);
            return list;
        }

        public SafeFileHandle GetFileHandle() => _dataFileStream.SafeFileHandle;

        public bool TryGetEntry(Guid assetId, out FileEntry entry)
        {
            int left = 0;
            int right = _fileCount - 1;
            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                FileEntry midEntry = GetEntryByIndex(mid);
                int cmp = midEntry.AssetId.CompareTo(assetId);
                if (cmp == 0) { entry = midEntry; return true; }
                if (cmp < 0) left = mid + 1; else right = mid - 1;
            }
            entry = default;
            return false;
        }

        public string? GetPathForAssetId(Guid id)
        {
            if (_nameTableOffset == 0) return null;
            byte* ptr = _basePtr + _nameTableOffset;
            byte* end = _basePtr + _fileLength;
            for (int i = 0; i < _fileCount; i++)
            {
                if (ptr >= end) break;
                byte[] guidBytes = new byte[16];
                Marshal.Copy((IntPtr)ptr, guidBytes, 0, 16);
                Guid entryGuid = new Guid(guidBytes);
                ptr += 16;
                int length = 0;
                int shift = 0;
                byte b;
                do { b = *ptr++; length |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
                if (entryGuid == id) return Encoding.UTF8.GetString(ptr, length);
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
                HasDebugNames = _nameTableOffset > 0,
                DependencyCount = _dependencyCount
            };

            for(int i=0; i < Math.Min(_fileCount, 2000); i++)
            {
                var e = GetEntryByIndex(i);
                uint methodMask = e.Flags & MASK_METHOD;
                string methodStr = methodMask switch {
                    METHOD_GDEFLATE => "GDeflate (GPU)",
                    METHOD_ZSTD => "Zstd (CPU)",
                    _ => "Store"
                };
                if ((e.Flags & FLAG_ENCRYPTED) != 0) methodStr += " [Enc]";
                if ((e.Flags & FLAG_STREAMING) != 0) methodStr += " [Stream]";

                string meta = "";
                if ((e.Flags & MASK_TYPE) == TYPE_TEXTURE)
                {
                    // Fixed: MetaData1/2 -> Meta1/2 to match struct definition
                    uint w = (e.Meta1 >> 16) & 0xFFFF;
                    uint h = e.Meta1 & 0xFFFF;
                    uint mips = (e.Meta2 >> 8) & 0xFF;
                    meta = $"{w}x{h} Mips:{mips}";
                }

                info.Entries.Add(new PackageEntryInfo
                {
                    Path = GetPathForAssetId(e.AssetId) ?? $"{e.AssetId}",
                    AssetId = e.AssetId,
                    Offset = e.DataOffset,
                    OriginalSize = e.OriginalSize,
                    CompressedSize = e.CompressedSize,
                    Method = methodStr,
                    Alignment = GetAlignmentFromFlags(e.Flags),
                    MetadataInfo = meta
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
