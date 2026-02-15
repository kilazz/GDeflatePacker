using System;
using System.Runtime.InteropServices;

namespace GPCK.Core
{
    /// <summary>
    /// Native wrapper for Zstandard (libzstd.dll).
    /// Used for high-ratio CPU compression of non-GPU assets (Scripts, JSON, Physics).
    /// </summary>
    public static class ZstdCodec
    {
        private const string DllName = "libzstd.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_compressBound(ulong srcSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_compress(IntPtr dst, ulong dstCapacity, IntPtr src, ulong srcSize, int compressionLevel);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong ZSTD_decompress(IntPtr dst, ulong dstCapacity, IntPtr src, ulong compressedSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ZSTD_isError(ulong code);

        public static bool IsAvailable()
        {
            try
            {
                // Dummy call to check if DLL loads
                ZSTD_compressBound(0);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}