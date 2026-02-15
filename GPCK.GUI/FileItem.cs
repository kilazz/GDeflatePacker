using System;
using GPCK.Core;

namespace GPCKGUI
{
    // Unified data class for both files-on-disk (to pack) and files-in-archive (to view)
    public class FileItem
    {
        // --- Common ---
        public string RelativePath { get; set; } = string.Empty;
        public Guid AssetId { get; set; } // v8 GUID
        public string Size { get; set; } = string.Empty;
        
        // --- Disk Mode (Source for packing) ---
        public string FilePath { get; set; } = string.Empty; // Full path on disk

        // --- Archive Mode (View/Extract) ---
        public bool IsArchiveEntry { get; set; } = false;
        public GameArchive? SourceArchive { get; set; } 
        public GameArchive.FileEntry? EntryInfo { get; set; }
        public long CompressedSizeBytes { get; set; }
        public string ManifestInfo { get; set; } = "";

        // --- UI Helpers ---
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

                if (e.OriginalSize == 0) return m;
                double ratio = (double)CompressedSizeBytes / e.OriginalSize * 100.0;
                return $"{m}\n{ratio:F0}%";
            }
        }
    }
}