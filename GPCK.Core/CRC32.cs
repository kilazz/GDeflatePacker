using System;

namespace GPCK.Core
{
    /// <summary>
    /// Fast table-based CRC32 implementation compatible with ZIP standard.
    /// Used to ensure data integrity within the archive.
    /// </summary>
    public static class Crc32
    {
        private static readonly uint[] Table;

        static Crc32()
        {
            const uint poly = 0xedb88320;
            Table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint temp = i;
                for (int j = 0; j < 8; j++)
                {
                    if ((temp & 1) == 1)
                        temp = (temp >> 1) ^ poly;
                    else
                        temp >>= 1;
                }
                Table[i] = temp;
            }
        }

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < data.Length; i++)
            {
                byte index = (byte)((crc & 0xff) ^ data[i]);
                crc = (crc >> 8) ^ Table[index];
            }
            return ~crc;
        }

        public static uint Compute(byte[] data) => Compute(new ReadOnlySpan<byte>(data));

        /// <summary>
        /// Updates a running CRC32 value with new data chunk.
        /// </summary>
        public static uint Update(uint currentCrc, ReadOnlySpan<byte> data)
        {
            uint crc = ~currentCrc;
            for (int i = 0; i < data.Length; i++)
            {
                byte index = (byte)((crc & 0xff) ^ data[i]);
                crc = (crc >> 8) ^ Table[index];
            }
            return ~crc;
        }
    }
}