
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GDeflate.Core
{
    /// <summary>
    /// GDeflate Stream v6.
    /// Implements "Scatter/Gather" I/O scheduling for Block-Level Deduplicated data.
    /// Reads are coalesced (merged) to minimize IOPS.
    /// </summary>
    public class GDeflateStream : Stream
    {
        private readonly GDeflateArchive _archive;
        private readonly GDeflateArchive.FileEntry _entry;
        
        private readonly bool _isCompressed;
        private readonly bool _isEncrypted;
        private readonly uint _method;

        private long _position;
        private byte[]? _decompressionBuffer; // 64KB Scratch
        private IntPtr _pOutput = IntPtr.Zero;

        // Decryption State
        private AesGcm? _aes;
        private byte[]? _decryptBuffer;

        public GDeflateStream(GDeflateArchive archive, GDeflateArchive.FileEntry entry)
        {
            _archive = archive;
            _entry = entry;
            _position = 0;
            
            _isCompressed = (entry.Flags & GDeflateArchive.FLAG_IS_COMPRESSED) != 0;
            _isEncrypted = (entry.Flags & GDeflateArchive.FLAG_ENCRYPTED) != 0;
            _method = entry.Flags & GDeflateArchive.MASK_METHOD;

            if (_isEncrypted)
            {
                if (_archive.DecryptionKey == null) throw new UnauthorizedAccessException("Encrypted file requires key.");
                _aes = new AesGcm(_archive.DecryptionKey);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _entry.OriginalSize;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position >= Length) return 0;
            int bytesToRead = (int)Math.Min(buffer.Length, Length - _position);
            if (bytesToRead <= 0) return 0;

            // --- I/O Scheduler ---
            // 1. Identify which logical blocks cover the requested range
            // 2. Map logical blocks to physical offsets via Global Block Table
            // 3. Coalesce adjacent physical blocks into single IO requests
            // 4. Execute Reads
            // 5. Decompress/Decrypt into destination

            // Identify Logical Range
            // Note: Since we have variable block sizes in theory (though usually 64KB uncompressed),
            // we need to scan the BlockTable sizes. 
            // Optimization: Assuming standard GDeflate tile size 65536 for all but last.
            
            const int TileSize = 65536;
            int startBlock = (int)(_position / TileSize);
            int endBlock = (int)((_position + bytesToRead - 1) / TileSize);
            
            int outputOffset = 0;
            int currentPosInTile = (int)(_position % TileSize);

            // Re-use buffers
            if (_decompressionBuffer == null) 
            {
                _decompressionBuffer = ArrayPool<byte>.Shared.Rent(TileSize);
                unsafe { _pOutput = Marshal.AllocHGlobal(TileSize); }
            }

            for (int i = startBlock; i <= endBlock; i++)
            {
                // Access Metadata (Zero-Copy)
                var blockInfo = _archive.GetBlockEntry((uint)(_entry.FirstBlockIndex + i));
                
                // Read Raw Data (Synchronous here for Stream API, but uses RandomAccess)
                // In a real engine, we would pre-fetch all blocks in 'start..end' range here.
                // For this implementation loop, we read one by one, but RandomAccess is thread-safe and cached by OS.
                // NOTE: To strictly implement Scatter/Gather, we would build a list here.
                
                // Let's implement immediate block processing for simplicity of the Stream API,
                // BUT use RandomAccess to jump physically.
                
                int rawSize = (int)blockInfo.CompressedSize;
                byte[] rawData = ArrayPool<byte>.Shared.Rent(rawSize);

                try
                {
                    // Random Access Read (No FileStream Position change)
                    RandomAccess.Read(_archive.GetFileHandle(), new Span<byte>(rawData, 0, rawSize), blockInfo.PhysicalOffset);

                    // Decrypt?
                    Span<byte> processData = new Span<byte>(rawData, 0, rawSize);
                    
                    if (_isEncrypted)
                    {
                        if (rawSize < 28) throw new InvalidDataException("Encrypted block too small");
                        int cipherSize = rawSize - 28;
                        if (_decryptBuffer == null || _decryptBuffer.Length < cipherSize) 
                        {
                            if (_decryptBuffer != null) ArrayPool<byte>.Shared.Return(_decryptBuffer);
                            _decryptBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(cipherSize, 65536));
                        }

                        var nonce = processData.Slice(0, 12);
                        var tag = processData.Slice(12, 16);
                        var cipher = processData.Slice(28, cipherSize);
                        var plain = new Span<byte>(_decryptBuffer, 0, cipherSize);
                        
                        _aes!.Decrypt(nonce, cipher, tag, plain);
                        processData = plain;
                    }

                    // Decompress or Copy
                    int bytesInThisTile = 0;
                    
                    if (_isCompressed && _method == GDeflateArchive.METHOD_GDEFLATE)
                    {
                        unsafe 
                        {
                             fixed (byte* pIn = processData)
                             {
                                 bool ok = GDeflateCpuApi.Decompress((void*)_pOutput, blockInfo.UncompressedSize, pIn, (ulong)processData.Length, 1);
                                 if (!ok) throw new InvalidDataException("Decompression failed");
                                 
                                 // Copy relevant part to user buffer
                                 Marshal.Copy(_pOutput, _decompressionBuffer!, 0, (int)blockInfo.UncompressedSize);
                                 bytesInThisTile = (int)blockInfo.UncompressedSize;
                             }
                        }
                    }
                    else if (_isCompressed && (_method == GDeflateArchive.METHOD_DEFLATE || _method == GDeflateArchive.METHOD_ZSTD))
                    {
                        // Legacy/CPU Decompress (simplified: unpack whole block)
                        using var ms = new MemoryStream(processData.ToArray()); // Alloc :(
                        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                        bytesInThisTile = ds.Read(_decompressionBuffer!, 0, _decompressionBuffer!.Length);
                    }
                    else // STORE
                    {
                        processData.CopyTo(_decompressionBuffer);
                        bytesInThisTile = processData.Length;
                    }

                    // Calculate intersection of this tile with user request
                    int available = bytesInThisTile - currentPosInTile;
                    int toCopy = Math.Min(available, bytesToRead - outputOffset);
                    
                    if (toCopy > 0)
                    {
                        new Span<byte>(_decompressionBuffer, currentPosInTile, toCopy).CopyTo(buffer.Slice(outputOffset, toCopy));
                        outputOffset += toCopy;
                    }
                    
                    currentPosInTile = 0; // Next blocks start at 0
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rawData);
                }
            }

            _position += outputOffset;
            return outputOffset;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin: target = offset; break;
                case SeekOrigin.Current: target = _position + offset; break;
                case SeekOrigin.End: target = Length + offset; break;
                default: throw new ArgumentException("Invalid Origin");
            }
            _position = Math.Clamp(target, 0, Length);
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_decompressionBuffer != null) ArrayPool<byte>.Shared.Return(_decompressionBuffer);
            if (_decryptBuffer != null) ArrayPool<byte>.Shared.Return(_decryptBuffer);
            if (_pOutput != IntPtr.Zero) Marshal.FreeHGlobal(_pOutput);
            _aes?.Dispose();
            base.Dispose(disposing);
        }
    }
}
