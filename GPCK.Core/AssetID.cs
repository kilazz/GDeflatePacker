using System;
using System.Security.Cryptography;
using System.Text;

namespace GPCK.Core
{
    /// <summary>
    /// Represents a 128-bit Global Asset Identifier (GUID).
    /// Used to decouple file paths from runtime resources.
    /// </summary>
    public static class AssetIdGenerator
    {
        // Namespace for Asset IDs (to ensure we don't collide with other GUIDs)
        private static readonly Guid AssetNamespace = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        /// <summary>
        /// Generates a deterministic Type-5 UUID (SHA-1) from the file path.
        /// This ensures the same path always produces the same AssetID.
        /// </summary>
        public static Guid Generate(string path)
        {
            if (string.IsNullOrEmpty(path)) return Guid.Empty;

            // Normalize path: Lowercase + Forward Slashes
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            byte[] nameBytes = Encoding.UTF8.GetBytes(normalized);
            byte[] namespaceBytes = AssetNamespace.ToByteArray();

            // Swap to Network Byte Order (RFC 4122)
            SwapByteOrder(namespaceBytes);

            // Compute SHA-1
            using var sha1 = SHA1.Create();
            sha1.TransformBlock(namespaceBytes, 0, namespaceBytes.Length, namespaceBytes, 0);
            sha1.TransformFinalBlock(nameBytes, 0, nameBytes.Length);
            byte[] hash = sha1.Hash!;

            byte[] newGuid = new byte[16];
            Array.Copy(hash, 0, newGuid, 0, 16);

            // Set version to 5
            newGuid[6] = (byte)((newGuid[6] & 0x0F) | (5 << 4));
            // Set variant to RFC 4122
            newGuid[8] = (byte)((newGuid[8] & 0x3F) | 0x80);

            // Swap back to host order for .NET Guid struct
            SwapByteOrder(newGuid);

            return new Guid(newGuid);
        }

        private static void SwapByteOrder(byte[] guid)
        {
            Swap(guid, 0, 3);
            Swap(guid, 1, 2);
            Swap(guid, 4, 5);
            Swap(guid, 6, 7);
        }

        private static void Swap(byte[] b, int i, int j)
        {
            (b[i], b[j]) = (b[j], b[i]);
        }
    }
}