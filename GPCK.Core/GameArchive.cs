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

        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _view;
        private readonly FileStream _dataFileStream;
        private readonly ArchiveHeader _header;

        public string FilePath { get; }
        public int FileCount => _header.FileCount;
        public byte[]? DecryptionKey { get; set; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ArchiveHeader {
            public int Magic, Version, FileCount, Padding;
            public long FileTableOffset, NameTableOffset;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct FileEntry {
            public Guid AssetId;
            public long DataOffset;
            public uint CompressedSize, OriginalSize, Flags, Meta1, Meta2;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct ChunkHeaderEntry { public uint CompressedSize, OriginalSize; }

        public const uint FLAG_IS_COMPRESSED = 1 << 0;
        public const uint FLAG_ENCRYPTED_META = 1 << 1;
        public const uint MASK_METHOD = 0x1C;
        public const uint METHOD_STORE = 0 << 2, METHOD_GDEFLATE = 1 << 2, METHOD_ZSTD = 2 << 2, METHOD_LZ4 = 3 << 2;
        public const uint MASK_TYPE = 0xE0;
        public const uint TYPE_TEXTURE = 1 << 5;
        public const uint FLAG_STREAMING = 1 << 8;
        public const uint MASK_ALIGNMENT = 0xFF000000;
        public const int SHIFT_ALIGNMENT = 24;

        public GameArchive(string path) {
            FilePath = path;
            _dataFileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _mmf = MemoryMappedFile.CreateFromFile(_dataFileStream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _view.Read(0, out _header);
        }

        public FileEntry GetEntryByIndex(int index) {
            _view.Read(_header.FileTableOffset + (index * 44), out FileEntry entry);
            return entry;
        }

        public SafeFileHandle GetFileHandle() => _dataFileStream.SafeFileHandle;

        public bool TryGetEntry(Guid id, out FileEntry entry) {
            int l = 0, r = FileCount - 1;
            while (l <= r) {
                int m = l + (r - l) / 2;
                FileEntry me = GetEntryByIndex(m);
                int c = me.AssetId.CompareTo(id);
                if (c == 0) { entry = me; return true; }
                if (c < 0) l = m + 1; else r = m - 1;
            }
            entry = default; return false;
        }

        public string? GetPathForAssetId(Guid id) {
            long p = _header.NameTableOffset;
            for (int i = 0; i < FileCount; i++) {
                byte[] gb = new byte[16]; _view.ReadArray(p, gb, 0, 16);
                ushort len = _view.ReadUInt16(p + 16);
                if (new Guid(gb) == id) { byte[] nb = new byte[len]; _view.ReadArray(p + 18, nb, 0, len); return Encoding.UTF8.GetString(nb); }
                p += 18 + len;
            }
            return null;
        }

        public Stream OpenRead(FileEntry entry) => new ArchiveStream(this, entry);

        public PackageInfo GetPackageInfo() {
            var info = new PackageInfo { FilePath = FilePath, Magic = MagicStr, Version = Version, FileCount = FileCount, TotalSize = _view.Capacity };
            for(int i=0; i < FileCount; i++) {
                var e = GetEntryByIndex(i);
                info.Entries.Add(new PackageEntryInfo {
                    Path = GetPathForAssetId(e.AssetId) ?? e.AssetId.ToString(),
                    AssetId = e.AssetId, Offset = e.DataOffset, OriginalSize = e.OriginalSize, CompressedSize = e.CompressedSize,
                    Method = (e.Flags & MASK_METHOD) switch { METHOD_GDEFLATE => "GDeflate", METHOD_ZSTD => "Zstd", METHOD_LZ4 => "LZ4", _ => "Store" }
                });
            }
            return info;
        }

        public void Dispose() { _view.Dispose(); _mmf.Dispose(); _dataFileStream.Dispose(); }
    }
}
