using System;
using System.Runtime.InteropServices;

namespace GPCK.Core
{
    public static class CodecLZ4
    {
        private const string DllName = "liblz4.dll";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LZ4_compressBound(int inputSize);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LZ4_compress_default(
            IntPtr src,
            IntPtr dst,
            int srcSize,
            int dstCapacity);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LZ4_compress_HC(
            IntPtr src,
            IntPtr dst,
            int srcSize,
            int dstCapacity,
            int compressionLevel);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int LZ4_decompress_safe(
            IntPtr src,
            IntPtr dst,
            int compressedSize,
            int dstCapacity);

        public static bool IsAvailable()
        {
            try
            {
                LZ4_compressBound(0);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}