using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GPCK.Core
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
            public int SplitOffset; 
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

        public static unsafe DdsBasicInfo? GetHeaderInfo(ReadOnlySpan<byte> fileData)
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

        /// <summary>
        /// Analyzes a DDS file and determines where to cut high-res mips from low-res tail.
        /// </summary>
        public static unsafe DdsSplitInfo? CalculateSplit(ReadOnlySpan<byte> fileData, int maxTailDim = 128)
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

                // DX10 Header check
                if (fourCC == 0x30315844) headerSize += 20;

                // Determine Block Size (Approximate for prototype, production needs strict format check)
                int blockSize = 16; // BC1-BC7 usually 8 or 16. Assuming 16 for safety in prototype.
                switch (fourCC)
                {
                    case 0x31545844: // DXT1
                        blockSize = 8; break;
                }

                int currentOffset = headerSize;
                int w = width;
                int h = height;
                int splitOffset = -1;
                int cutMips = 0;

                for (int i = 0; i < mips; i++)
                {
                    // If we reached dimensions small enough for the tail
                    if (splitOffset == -1 && w <= maxTailDim && h <= maxTailDim)
                    {
                        splitOffset = currentOffset;
                        cutMips = i;
                        break;
                    }

                    int blocksW = Math.Max(1, (w + 3) / 4);
                    int blocksH = Math.Max(1, (h + 3) / 4);
                    int mipSize = blocksW * blocksH * blockSize;

                    currentOffset += mipSize;
                    if (w > 1) w /= 2;
                    if (h > 1) h /= 2;
                }

                if (splitOffset == -1 || splitOffset >= fileData.Length) return null;

                return new DdsSplitInfo
                {
                    HeaderSize = headerSize,
                    SplitOffset = splitOffset,
                    CutMipCount = cutMips,
                    LowResWidth = Math.Max(1, width >> cutMips),
                    LowResHeight = Math.Max(1, height >> cutMips),
                    LowResMipCount = mips - cutMips
                };
            }
        }

        /// <summary>
        /// Splits the DDS into Resident (Tail) and Streaming (Payload) parts.
        /// Returns a pooled buffer containing [Tail][Payload].
        /// </summary>
        public static unsafe byte[] ProcessTextureForStreaming(byte[] source, out int tailSize)
        {
            var info = CalculateSplit(source);
            if (info == null)
            {
                tailSize = source.Length;
                return source;
            }

            // Structure: [Header (Modified)] [Tail Mips] [High Res Mips]
            // We actually keep the order: TailFirst? No.
            // Standard engine practice: High mips are large, Tail is small.
            // For GPCK Linear: [Tail (Header+SmallMips)] .... [Payload (BigMips)]
            
            int payloadSize = info.SplitOffset - info.HeaderSize;
            int tailMipsSize = source.Length - info.SplitOffset;
            
            tailSize = info.HeaderSize + tailMipsSize;
            int totalSize = tailSize + payloadSize;

            byte[] result = ArrayPool<byte>.Shared.Rent(totalSize);
            
            // 1. Copy Header
            Array.Copy(source, 0, result, 0, info.HeaderSize);
            
            // 2. Patch Header in Result (Tail dimensions)
            fixed(byte* p = result)
            {
                DDS_HEADER* h = (DDS_HEADER*)(p + 4);
                h->dwWidth = (uint)info.LowResWidth;
                h->dwHeight = (uint)info.LowResHeight;
                h->dwMipMapCount = (uint)info.LowResMipCount;
                h->dwPitchOrLinearSize = 0; // Invalid after resize
            }

            // 3. Copy Tail Mips (Resident) immediately after header
            Array.Copy(source, info.SplitOffset, result, info.HeaderSize, tailMipsSize);

            // 4. Copy Payload (Streaming Mips) after tail
            Array.Copy(source, info.HeaderSize, result, tailSize, payloadSize);

            return result; // Caller must slice to 'totalSize' and return to pool later if possible, or copy.
        }
    }
}