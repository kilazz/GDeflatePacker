using System;
using GPCK.Core;

namespace GPCKGUI
{
    public class FileItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public Guid AssetId { get; set; }
        public string Size { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public bool IsArchiveEntry { get; set; } = false;
        public GameArchive? SourceArchive { get; set; }
        public GameArchive.FileEntry? EntryInfo { get; set; }
        public long CompressedSizeBytes { get; set; }
        public string ManifestInfo { get; set; } = "";

        public string TypeIcon => IsArchiveEntry ? "📦" : "📄";

        public string DisplayName
        {
            get
            {
                if (IsArchiveEntry) return $"{RelativePath}\n[{AssetId}]";
                return RelativePath;
            }
        }

        public string CompressionInfo
        {
            get
            {
                if (!IsArchiveEntry || !EntryInfo.HasValue) return "Pending";
                var e = EntryInfo.Value;

                uint method = e.Flags & GameArchive.MASK_METHOD;
                string m = method switch
                {
                    GameArchive.METHOD_GDEFLATE => "GDeflate (GPU)",
                    GameArchive.METHOD_ZSTD => "Zstd (CPU)",
                    GameArchive.METHOD_LZ4 => "LZ4 (Fast)",
                    _ => "Store"
                };

                if ((e.Flags & GameArchive.FLAG_ENCRYPTED_META) != 0) m += " [Enc-Meta]";

                if (e.OriginalSize == 0) return m;
                double ratio = (double)CompressedSizeBytes / e.OriginalSize * 100.0;
                return $"{m}\n{ratio:F0}%";
            }
        }
    }
}
