using System;
using System.Text;

namespace GPCK.Core
{
    /// <summary>
    /// Implements FNV-1a 64-bit hashing.
    /// Standard for game assets (Unreal/Frostbite) to reduce string overhead.
    /// </summary>
    public static class PathHasher
    {
        private const ulong OffsetBasis = 14695981039346656037;
        private const ulong Prime = 1099511628211;

        /// <summary>
        /// Computes a deterministic hash for a file path.
        /// Paths are normalized to lowercase and forward slashes to ensure consistency across OS.
        /// </summary>
        public static ulong Hash(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            // Normalize: "Assets\Texture.png" -> "assets/texture.png"
            // This ensures that Windows/Linux paths result in the same ID.
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            byte[] bytes = Encoding.UTF8.GetBytes(normalized);

            ulong hash = OffsetBasis;
            foreach (byte b in bytes)
            {
                hash ^= b;
                hash *= Prime;
            }

            return hash;
        }
    }
}