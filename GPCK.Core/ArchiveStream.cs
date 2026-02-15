using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GPCK.Core
{
    /// <summary>
    /// Next-Gen Asset Stream.
    /// Supports:
    /// 1. Standard Stream (for CPU assets like JSON/Scripts)
    /// 2. Direct-to-Native (for GPU assets like Textures/Geom)
    /// 3. Virtual Texture Streaming (Tail vs Payload)
    /// </summary>
    public class ArchiveStream : Stream
    {
        private readonly GameArchive _archive;
        private readonly GameArchive.FileEntry _entry;
        
        private readonly bool _isCompressed;
        private readonly bool _isEncrypted;
        private readonly uint _method;
        private readonly bool _isTexture;
        private readonly int _tailSize; // Size of Resident data (Header + Small Mips)

        private long _position;
        private AesGcm? _aes;
        
        // Cache for standard stream operations
        private byte[]? _cpuCache;

        public ArchiveStream(GameArchive archive, GameArchive.FileEntry entry)
        {
            _archive = archive;
            _entry = entry;
            _position = 0;
            
            _isCompressed = (entry.Flags & GameArchive.FLAG_IS_COMPRESSED) != 0;
            _isEncrypted = (entry.Flags & GameArchive.FLAG_ENCRYPTED) != 0;
            _method = entry.Flags & GameArchive.MASK_METHOD;
            _isTexture = (entry.Flags & GameArchive.MASK_TYPE) == GameArchive.TYPE_TEXTURE;

            // Meta2: Low 24 bits = Tail Size (if split)
            _tailSize = (int)(entry.Meta2 & 0x00FFFFFF);
            if (_tailSize == 0) _tailSize = (int)entry.OriginalSize; // Full resident if not split

            if (_isEncrypted && _archive.DecryptionKey != null)
            {
                _aes = new AesGcm(_archive.DecryptionKey, 16);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _entry.OriginalSize;
        public override long Position { get => _position; set => _position = value; }

        // --- Standard Stream Implementation (For Compatibility / CPU Assets) ---

        public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            // If it's a small CPU asset, cache it in managed memory
            if (_cpuCache == null)
            {
                // Allocate unmanaged, decompress, copy to managed, free unmanaged
                // This is the fallback path. Performance critical assets use ReadToNative.
                _cpuCache = ArrayPool<byte>.Shared.Rent((int)_entry.OriginalSize);
                unsafe
                {
                    fixed (byte* ptr = _cpuCache)
                    {
                        ReadToNative((IntPtr)ptr, _entry.OriginalSize);
                    }
                }
            }

            int available = (int)(Length - _position);
            int toCopy = Math.Min(buffer.Length, available);
            if (toCopy <= 0) return 0;

            new Span<byte>(_cpuCache, (int)_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        // --- DirectStorage Emulation (Zero-Copy) ---

        /// <summary>
        /// Reads decompressed data directly into a native memory pointer.
        /// This mimics DirectStorage: File -> SysRAM (Compressed) -> GDeflate -> VRAM (Simulated by 'dest').
        /// </summary>
        public unsafe void ReadToNative(IntPtr destination, long size)
        {
            if (size == 0) return;
            
            // 1. Read Compressed Data from Disk (bypass OS cache if possible, but here using FileStream)
            // Use pooled buffer for the compressed read to avoid GC
            int compressedSize = (int)_entry.CompressedSize;
            byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedSize);

            try
            {
                RandomAccess.Read(_archive.GetFileHandle(), compressedBuffer.AsSpan(0, compressedSize), _entry.DataOffset);
                
                // 2. Decrypt in place if needed
                ReadOnlySpan<byte> dataToDecompress = compressedBuffer.AsSpan(0, compressedSize);
                
                if (_isEncrypted && _aes != null)
                {
                    // Decryption unfortunately requires a copy or mutable span. 
                    // AesGcm in .NET standard requires separate tag.
                    var nonce = dataToDecompress.Slice(0, 12);
                    var tag = dataToDecompress.Slice(12, 16);
                    var ciphertext = dataToDecompress.Slice(28);
                    
                    // Decrypt directly into the same buffer offset 28? No, different size.
                    // For prototype, decrypt to separate temp buffer.
                    byte[] decryptBuffer = ArrayPool<byte>.Shared.Rent(ciphertext.Length);
                    _aes.Decrypt(nonce, ciphertext, tag, decryptBuffer.AsSpan(0, ciphertext.Length));
                    dataToDecompress = decryptBuffer.AsSpan(0, ciphertext.Length);
                    
                    // Note: We'd return decryptBuffer to pool in a real scenario inside a finally block
                }

                // 3. Decompress directly to Native Destination
                if (_isCompressed)
                {
                    fixed (byte* pSrc = dataToDecompress)
                    {
                        if (_method == GameArchive.METHOD_GDEFLATE)
                        {
                            // GDeflate direct to native
                            if (!GDeflateCodec.Decompress(
                                (void*)destination, 
                                (ulong)size, 
                                pSrc, 
                                (ulong)dataToDecompress.Length, 
                                1)) // 1 worker for now
                            {
                                throw new IOException("GDeflate Decompression Failed");
                            }
                        }
                        else if (_method == GameArchive.METHOD_ZSTD)
                        {
                            // Zstd direct to native
                            ulong res = ZstdCodec.ZSTD_decompress(
                                destination, 
                                (ulong)size, 
                                (IntPtr)pSrc, 
                                (ulong)dataToDecompress.Length);
                            
                            if (ZstdCodec.ZSTD_isError(res) != 0)
                                throw new IOException("Zstd Decompression Failed");
                        }
                    }
                }
                else
                {
                    // Store method: Copy directly to native
                    fixed (byte* pSrc = dataToDecompress)
                    {
                        Buffer.MemoryCopy(pSrc, (void*)destination, size, Math.Min(size, dataToDecompress.Length));
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedBuffer);
            }
        }

        /// <summary>
        /// Reads only the resident tail (Header + Small Mips)
        /// </summary>
        public void ReadTail(Span<byte> buffer)
        {
             // Logic would be similar to ReadToNative but calculating the offset/size within the compressed blob
             // This is complex with monolithic compression.
             // Current Packer implementation packs Tail + Payload into ONE compressed stream for simplicity of IO.
             // To support true separate streaming, Read() would need to decompress the whole thing 
             // and only copy the relevant parts, OR we compress them as separate blocks in the packer.
             // 
             // Assuming packer compresses them as one block for now (simplest upgrade).
             // We decode all, but copy only tail.
             
             int bytesToRead = Math.Min(buffer.Length, _tailSize);
             int totalRead = 0;
             while (totalRead < bytesToRead)
             {
                 int read = Read(buffer.Slice(totalRead, bytesToRead - totalRead));
                 if (read == 0) break;
                 totalRead += read;
             }
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
            if (_cpuCache != null)
            {
                ArrayPool<byte>.Shared.Return(_cpuCache);
                _cpuCache = null;
            }
            _aes?.Dispose();
            base.Dispose(disposing);
        }
    }
}