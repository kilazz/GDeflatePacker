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

        [StructLayout(LayoutKind.Sequential)]
        public struct DDS_HEADER_DXT10
        {
            public uint dxgiFormat;
            public uint resourceDimension;
            public uint miscFlag;
            public uint arraySize;
            public uint miscFlags2;
        }

        public class DdsSplitInfo
        {
            public int HeaderSize;
            public int DataStartOffset;
            public int SplitDataOffset; // Where the "Tail" (small mips) begins in the file
            
            // Info for the patched header (Low Res)
            public int LowResWidth;
            public int LowResHeight;
            public int LowResMipCount;
            
            public int CutMipCount; // Number of mips moved to High Res file
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

                // Don't split small textures
                if (width <= maxTailDim && height <= maxTailDim) return null;

                int headerSize = 128; // 4 + 124
                uint fourCC = header->ddspf.dwFourCC;

                // Handle DX10 header
                if (fourCC == 0x30315844) // 'DX10'
                {
                    headerSize += 20;
                    if (fileData.Length < headerSize) return null;
                }

                // Determine Block Size
                int blockSize = 0;
                bool isBlockCompressed = false;

                // Simple FourCC check
                switch (fourCC)
                {
                    case 0x31545844: // DXT1
                    case 0x30315844: // DX10 (Assume BC7/BC6/BC5/BC4/etc which are mostly 16 or 8)
                        // This is a simplification. For production, parsing DXGI format is needed.
                        // Assuming common BC formats for now.
                        // DXT1/BC1 = 8 bytes.
                        // Others = 16 bytes.
                        // Let's refine DX10 check if possible, otherwise heuristic.
                        blockSize = 16; 
                        isBlockCompressed = true;
                        break;
                    case 0x33545844: // DXT3
                    case 0x35545844: // DXT5
                        blockSize = 16;
                        isBlockCompressed = true;
                        break;
                    case 0x31545844: // DXT1 again (endianness?) 
                         blockSize = 8;
                         isBlockCompressed = true;
                         break;
                }
                
                if (fourCC == 0x31545844) blockSize = 8; // Explicit DXT1

                // If uncompressed/unknown, skip splitting for safety
                if (!isBlockCompressed && blockSize == 0) return null;
                if (blockSize == 0) blockSize = 16; // Default fallback to BC7 size for DX10

                // Calculate Offsets
                int currentOffset = headerSize;
                int w = width;
                int h = height;
                int splitOffset = -1;
                int cutMips = 0;

                for (int i = 0; i < mips; i++)
                {
                    // Check if we reached the tail (low res)
                    if (splitOffset == -1 && w <= maxTailDim && h <= maxTailDim)
                    {
                        splitOffset = currentOffset;
                        cutMips = i;
                    }

                    // Calc mip size
                    int mipSize;
                    if (isBlockCompressed)
                    {
                        int blocksW = Math.Max(1, (w + 3) / 4);
                        int blocksH = Math.Max(1, (h + 3) / 4);
                        mipSize = blocksW * blocksH * blockSize;
                    }
                    else
                    {
                        // Assume 4 bytes per pixel (RGBA) if not BC
                        mipSize = w * h * 4; 
                    }

                    currentOffset += mipSize;
                    
                    if (w > 1) w /= 2;
                    if (h > 1) h /= 2;
                }

                if (splitOffset == -1) return null; // Couldn't split (maybe all large?)

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
                
                // Update to low-res dimensions
                header->dwWidth = (uint)info.LowResWidth;
                header->dwHeight = (uint)info.LowResHeight;
                header->dwMipMapCount = (uint)info.LowResMipCount;
                
                // Update Pitch/LinearSize if needed (optional but good practice)
                // Leaving as is usually works for engines, but zeroing it forces recalculation
                header->dwPitchOrLinearSize = 0; 
            }
        }
    }
}