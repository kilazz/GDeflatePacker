using System;
using GDeflate.Core;

namespace GDeflateGUI
{
    // Unified data class for both files-on-disk (to pack) and files-in-archive (to view)
    public class FileItem
    {
        // --- Common ---
        public string RelativePath { get; set; } = string.Empty; // e.g. "textures/hero.png"
        public string Size { get; set; } = string.Empty;
        
        // --- Disk Mode (Source for packing) ---
        public string FilePath { get; set; } = string.Empty; // Full path on disk

        // --- Archive Mode (View/Extract) ---
        public bool IsArchiveEntry { get; set; } = false;
        public GDeflateArchive? SourceArchive { get; set; } // Reference to keep the archive open
        public GDeflateArchive.FileEntry? EntryInfo { get; set; }
        
        // --- UI Helpers ---
        public string TypeIcon => IsArchiveEntry ? "📦" : "📄";
        
        public string CompressionInfo 
        {
            get
            {
                if (!IsArchiveEntry || !EntryInfo.HasValue) return "Ready to Pack";
                return GetCompressionLabel(EntryInfo.Value);
            }
        }

        private static string GetCompressionLabel(GDeflateArchive.FileEntry e)
        {
            if ((e.Flags & GDeflateArchive.FLAG_IS_COMPRESSED) == 0) return "Store (Raw)";
            
            uint method = e.Flags & GDeflateArchive.MASK_COMPRESSION_METHOD;
            string m = method switch
            {
                GDeflateArchive.METHOD_GDEFLATE => "GDeflate (GPU)",
                GDeflateArchive.METHOD_DEFLATE => "Deflate (CPU)",
                GDeflateArchive.METHOD_ZSTD => "Zstd",
                _ => "Unknown"
            };

            // Calculate ratio
            double ratio = (double)e.CompressedSize / e.OriginalSize * 100.0;
            return $"{m} ({ratio:F0}%)";
        }
    }
}