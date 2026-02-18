using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GPCK.Core
{
    public static class CodecGDeflate
    {
        private const string DllName = "GDeflate";

        static CodecGDeflate()
        {
            // With the standard 'runtimes/win-x64/native/' structure,
            // .NET handles resolution automatically. Custom resolver removed.
        }

        public static bool IsAvailable()
        {
            try
            {
                // Verify we can resolve the bound function
                CompressBound(0);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GDeflateCompressBound")]
        public static extern ulong CompressBound(ulong size);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GDeflateCompress")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static unsafe extern bool Compress(
            void* output,
            ref ulong outputSize,
            void* input,
            ulong inputSize,
            uint level,
            uint flags);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "GDeflateDecompress")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static unsafe extern bool Decompress(
            void* output,
            ulong outputSize,
            void* input,
            ulong inputSize,
            uint numWorkers);
    }
}
