using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GPCK.Core
{
    public class GameArchive : IDisposable
    {
        public const int Version = 1;
        public const string MagicStr = "GPCK";
        private const int FileEntrySize = 44;

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly FileStream _dataFileStream;

        private readonly ArchiveHeader _header;

        private Dictionary<Guid, List<DependencyEntry>>? _dependencyLookup;

        public string FilePath { get; }
        public int FileCount => _header.FileCount;
        public byte[]? DecryptionKey { get; set; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ArchiveHeader
        {
            public int Magic; // "GPCK"
            public int Version;
            public int FileCount;
            public int Padding1;
            public int DependencyCount;
            public int Padding2; // Align to 24
            public long FileTableOffset;
            public long Reserved;
            public long NameTableOffset;
            public long DependencyTableOffset;
        }

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
        public const uint METHOD_LZ4         = 3 << 2;
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

        public GameArchive(string path)
        {
            FilePath = path;
            var fi = new FileInfo(path);
            if (!fi.Exists) throw new FileNotFoundException(path);
            long fileLength = fi.Length;

            if (fileLength < Marshal.SizeOf<ArchiveHeader>()) throw new InvalidDataException("File too short");

            _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

            _dataFileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);

            // Read Header safely
            _view.Read(0, out _header);

            // Verify Magic
            string magicRead = Encoding.ASCII.GetString(BitConverter.GetBytes(_header.Magic));
            if (magicRead != MagicStr) throw new InvalidDataException($"Invalid Magic: {magicRead}");
            if (_header.Version != Version) throw new NotSupportedException($"Version mismatch. Archive: {_header.Version}, Engine: {Version}");

            // Validate offsets
            if (_header.FileTableOffset < 0 || _header.FileTableOffset >= fileLength) throw new InvalidDataException("Invalid File Table Offset");
        }

        public FileEntry GetEntryByIndex(int index)
        {
            if (index < 0 || index >= FileCount) throw new IndexOutOfRangeException();

            long offset = _header.FileTableOffset + ((long)index * FileEntrySize);

            FileEntry entry;
            _view.Read(offset, out entry);
            return entry;
        }

        public List<DependencyEntry> GetDependencies()
        {
            int count = _header.DependencyCount;
            var list = new List<DependencyEntry>(count);
            if (count == 0 || _header.DependencyTableOffset == 0) return list;

            long offset = _header.DependencyTableOffset;
            int step = Marshal.SizeOf<DependencyEntry>();

            for(int i=0; i<count; i++)
            {
                DependencyEntry dep;
                _view.Read(offset + (i * step), out dep);
                list.Add(dep);
            }
            return list;
        }

        public List<DependencyEntry> GetDependenciesForAsset(Guid assetId)
        {
            if (_dependencyLookup == null)
            {
                _dependencyLookup = new Dictionary<Guid, List<DependencyEntry>>();
                var deps = GetDependencies();
                foreach(var d in deps)
                {
                    if (!_dependencyLookup.ContainsKey(d.SourceAssetId))
                        _dependencyLookup[d.SourceAssetId] = new List<DependencyEntry>();
                    _dependencyLookup[d.SourceAssetId].Add(d);
                }
            }

            if (_dependencyLookup.TryGetValue(assetId, out var list)) return list;
            return new List<DependencyEntry>();
        }

        public SafeFileHandle GetFileHandle() => _dataFileStream.SafeFileHandle;

        public bool TryGetEntry(Guid assetId, out FileEntry entry)
        {
            int left = 0;
            int right = FileCount - 1;
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
            if (_header.NameTableOffset == 0) return null;

            // Safe implementation using View Accessor without raw pointers
            // Note: This is slower than raw pointers but safer. For production tool, unsafe is fine,
            // but for this refactor we prioritize safety.

            long ptr = _header.NameTableOffset;
            long end = _view.Capacity;

            for (int i = 0; i < FileCount; i++)
            {
                if (ptr >= end - 16) break;

                // Read Guid
                byte[] guidBytes = new byte[16];
                _view.ReadArray(ptr, guidBytes, 0, 16);
                Guid entryGuid = new Guid(guidBytes);
                ptr += 16;

                // Read VarInt Length
                int length = 0;
                int shift = 0;
                byte b;
                do {
                    if (ptr >= end) return null;
                    b = _view.ReadByte(ptr++);
                    length |= (b & 0x7F) << shift;
                    shift += 7;
                } while ((b & 0x80) != 0);

                if (ptr + length > end) return null;

                if (entryGuid == id)
                {
                    byte[] strBytes = new byte[length];
                    _view.ReadArray(ptr, strBytes, 0, length);
                    return Encoding.UTF8.GetString(strBytes);
                }
                ptr += length;
            }
            return null;
        }

        public Stream OpenRead(FileEntry entry)
        {
            if (entry.OriginalSize == 0) return new MemoryStream();
            return new ArchiveStream(this, entry);
        }

        public PackageInfo GetPackageInfo()
        {
            var info = new PackageInfo
            {
                FilePath = FilePath,
                Magic = MagicStr,
                Version = Version,
                FileCount = FileCount,
                TotalSize = _view.Capacity,
                HasDebugNames = _header.NameTableOffset > 0,
                DependencyCount = _header.DependencyCount
            };

            for(int i=0; i < Math.Min(FileCount, 5000); i++)
            {
                var e = GetEntryByIndex(i);
                uint methodMask = e.Flags & MASK_METHOD;
                string methodStr = methodMask switch {
                    METHOD_GDEFLATE => "GDeflate",
                    METHOD_ZSTD => "Zstd",
                    METHOD_LZ4 => "LZ4",
                    _ => "Store"
                };
                if ((e.Flags & FLAG_ENCRYPTED) != 0) methodStr += " [Enc]";
                if ((e.Flags & FLAG_STREAMING) != 0) methodStr += " [Stream]";

                string meta = "";
                if ((e.Flags & MASK_TYPE) == TYPE_TEXTURE)
                {
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
            _view?.Dispose();
            _mmf?.Dispose();
            _dataFileStream?.Dispose();
        }
    }
}
