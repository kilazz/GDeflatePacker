using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GDeflate.Core
{
    public static class DdsUtils
    {
        private const uint Magic = 0x20534444; // "DDS "

        [StructLayout(LayoutKind.Sequential)]
        public struct DDS_PIXELFORMAT
        {
            public uint dwSize;
            public uint dwFlags;
            public uint dwFourCC;
            public uint dwRGBBitCount;
            public uint dwRBitMask;
            public uint dwGBitMask;
            public uint dwBBitMask;
            public uint dwABitMask;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DDS_HEADER
        {
            public uint dwSize;
            public uint dwFlags;
            public uint dwHeight;
            public uint dwWidth;
            public uint dwPitchOrLinearSize;
            public uint dwDepth;
            public uint dwMipMapCount;
            public unsafe fixed uint dwReserved1[11];
            public DDS_PIXELFORMAT ddspf;
            public uint dwCaps;
            public uint dwCaps2;
            public uint dwCaps3;
            public uint dwCaps4;
            public uint dwReserved2;
        }

        public class DdsSplitInfo
        {
            public int HeaderSize;
            public int DataStartOffset;
            public int SplitDataOffset; 
            public int LowResWidth;
            public int LowResHeight;
            public int LowResMipCount;
            public int CutMipCount; 
        }

        public struct DdsBasicInfo
        {
            public int Width;
            public int Height;
            public int MipCount;
        }

        public static unsafe DdsBasicInfo? GetHeaderInfo(byte[] fileData)
        {
            if (fileData.Length < 128) return null;
            fixed (byte* p = fileData)
            {
                if (*(uint*)p != Magic) return null;
                DDS_HEADER* header = (DDS_HEADER*)(p + 4);
                return new DdsBasicInfo
                {
                    Width = (int)header->dwWidth,
                    Height = (int)header->dwHeight,
                    MipCount = Math.Max(1, (int)header->dwMipMapCount)
                };
            }
        }

        public static unsafe DdsSplitInfo? CalculateSplit(byte[] fileData, int maxTailDim = 128)
        {
            if (fileData.Length < 128) return null;

            fixed (byte* p = fileData)
            {
                if (*(uint*)p != Magic) return null;
                
                DDS_HEADER* header = (DDS_HEADER*)(p + 4);
                if (header->dwSize != 124) return null;

                int width = (int)header->dwWidth;
                int height = (int)header->dwHeight;
                int mips = (int)header->dwMipMapCount;
                if (mips == 0) mips = 1;

                if (width <= maxTailDim && height <= maxTailDim) return null;

                int headerSize = 128;
                uint fourCC = header->ddspf.dwFourCC;

                if (fourCC == 0x30315844) headerSize += 20;

                int blockSize = 16; // Simplified estimation for prototype
                
                int currentOffset = headerSize;
                int w = width;
                int h = height;
                int splitOffset = -1;
                int cutMips = 0;

                for (int i = 0; i < mips; i++)
                {
                    if (splitOffset == -1 && w <= maxTailDim && h <= maxTailDim)
                    {
                        splitOffset = currentOffset;
                        cutMips = i;
                    }

                    int blocksW = Math.Max(1, (w + 3) / 4);
                    int blocksH = Math.Max(1, (h + 3) / 4);
                    int mipSize = blocksW * blocksH * blockSize;

                    currentOffset += mipSize;
                    if (w > 1) w /= 2;
                    if (h > 1) h /= 2;
                }

                if (splitOffset == -1) return null;

                return new DdsSplitInfo
                {
                    HeaderSize = headerSize,
                    DataStartOffset = headerSize,
                    SplitDataOffset = splitOffset,
                    CutMipCount = cutMips,
                    LowResWidth = Math.Max(1, width >> cutMips),
                    LowResHeight = Math.Max(1, height >> cutMips),
                    LowResMipCount = mips - cutMips
                };
            }
        }

        public static unsafe void PatchHeader(byte[] headerBytes, DdsSplitInfo info)
        {
            if (headerBytes.Length < 128) return;
            fixed (byte* p = headerBytes)
            {
                DDS_HEADER* header = (DDS_HEADER*)(p + 4);
                header->dwWidth = (uint)info.LowResWidth;
                header->dwHeight = (uint)info.LowResHeight;
                header->dwMipMapCount = (uint)info.LowResMipCount;
                header->dwPitchOrLinearSize = 0; 
            }
        }
    }
}