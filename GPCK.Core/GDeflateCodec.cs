using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GDeflate.Core
{
    public static class GDeflateCodec
    {
        private const string DllName = "GDeflate.dll";

        static GDeflateCodec()
        {
            NativeLibrary.SetDllImportResolver(typeof(GDeflateCodec).Assembly, CustomDllResolver);
        }

        private static IntPtr CustomDllResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == DllName)
            {
                string root = AppContext.BaseDirectory;

                // 1. Check root directory
                string rootPath = Path.Combine(root, DllName);
                if (File.Exists(rootPath) && NativeLibrary.TryLoad(rootPath, out IntPtr rootHandle))
                    return rootHandle;

                // 2. Check libs subdirectory
                string libsPath = Path.Combine(root, "libs", DllName);
                if (File.Exists(libsPath) && NativeLibrary.TryLoad(libsPath, out IntPtr libsHandle))
                    return libsHandle;

                // 3. Fallback to default loading
                if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out IntPtr defaultHandle))
                    return defaultHandle;
            }
            return IntPtr.Zero;
        }

        public static bool IsAvailable()
        {
            return NativeLibrary.TryLoad(DllName, typeof(GDeflateCodec).Assembly, null, out IntPtr handle) && handle != IntPtr.Zero;
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