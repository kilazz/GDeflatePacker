using System;

namespace GDeflateGUI
{
    // Simple data class to hold file information for the UI list
    public class FileItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Size { get; set; } = string.Empty;
    }
}
