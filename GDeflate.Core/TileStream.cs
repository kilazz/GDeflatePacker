using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GDeflate.Core
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TileStreamHeader
    {
        public byte id;
        public byte magic;
        public ushort numTiles;
        public uint packedFields;

        public const int kDefaultTileSize = 64 * 1024; // 64KB

        public ulong GetUncompressedSize()
        {
            // Extract lastTileSize (18 bits starting at bit 2)
            uint lastTileSize = (packedFields >> 2) & 0x3FFFF;

            ulong size = (ulong)numTiles * kDefaultTileSize;

            if (lastTileSize != 0)
            {
                size -= (ulong)(kDefaultTileSize - lastTileSize);
            }
            return size;
        }

        public static TileStreamHeader ReadFromBytes(byte[] data)
        {
            if (data == null || data.Length < 8)
                throw new InvalidDataException("Input data is too small.");

            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<TileStreamHeader>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public static TileStreamHeader ReadFromSpan(ReadOnlySpan<byte> data)
        {
            if (data.Length < 8) throw new InvalidDataException("Input data too small.");
            return MemoryMarshal.Read<TileStreamHeader>(data);
        }
    }
}
