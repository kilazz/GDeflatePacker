using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace GPCK.Core
{
    public class ArchiveStream : Stream
    {
        private readonly GameArchive _archive;
        private readonly GameArchive.FileEntry _entry;
        
        private readonly bool _isCompressed;
        private readonly bool _isEncrypted;
        private readonly uint _method;
        private long _position;
        private AesGcm? _aes;
        private byte[]? _cpuCache;

        public ArchiveStream(GameArchive archive, GameArchive.FileEntry entry)
        {
            _archive = archive;
            _entry = entry;
            _position = 0;
            
            _isCompressed = (entry.Flags & GameArchive.FLAG_IS_COMPRESSED) != 0;
            _isEncrypted = (entry.Flags & GameArchive.FLAG_ENCRYPTED) != 0;
            _method = entry.Flags & GameArchive.MASK_METHOD;

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

        public override int Read(byte[] buffer, int offset, int count) => Read(new Span<byte>(buffer, offset, count));

        public override int Read(Span<byte> buffer)
        {
            // Fallback for small CPU reads: Cache the whole file uncompressed in memory.
            // For large streaming assets, one should use specific ReadToNative logic (omitted for brevity in this generic Stream).
            if (_cpuCache == null)
            {
                FillCache();
            }

            int available = (int)(Length - _position);
            int toCopy = Math.Min(buffer.Length, available);
            if (toCopy <= 0) return 0;

            new Span<byte>(_cpuCache, (int)_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
             // In a real implementation, we would async stream the compressed data from disk
             // and decompress chunks. For this prototype, we still fill the cache but wrapping in Task.Run.
             if (_cpuCache == null)
             {
                 await Task.Run(FillCache, cancellationToken);
             }
             return Read(new Span<byte>(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_cpuCache == null)
            {
                 // Heavy lifting offloaded
                 return new ValueTask<int>(Task.Run(() => { FillCache(); return Read(buffer.Span); }, cancellationToken));
            }
            return new ValueTask<int>(Read(buffer.Span));
        }

        private void FillCache()
        {
            _cpuCache = ArrayPool<byte>.Shared.Rent((int)_entry.OriginalSize);
            unsafe
            {
                fixed (byte* ptr = _cpuCache)
                {
                    ReadToNative((IntPtr)ptr, _entry.OriginalSize);
                }
            }
        }

        public unsafe void ReadToNative(IntPtr destination, long size)
        {
            if (size == 0) return;
            
            int compressedSize = (int)_entry.CompressedSize;
            byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedSize);

            try
            {
                // Synchronous random read from the underlying FileStream handle
                RandomAccess.Read(_archive.GetFileHandle(), compressedBuffer.AsSpan(0, compressedSize), _entry.DataOffset);
                
                ReadOnlySpan<byte> dataToDecompress = compressedBuffer.AsSpan(0, compressedSize);
                
                if (_isEncrypted && _aes != null)
                {
                    var nonce = dataToDecompress.Slice(0, 12);
                    var tag = dataToDecompress.Slice(12, 16);
                    var ciphertext = dataToDecompress.Slice(28);
                    
                    byte[] decryptBuffer = ArrayPool<byte>.Shared.Rent(ciphertext.Length);
                    _aes.Decrypt(nonce, ciphertext, tag, decryptBuffer.AsSpan(0, ciphertext.Length));
                    dataToDecompress = decryptBuffer.AsSpan(0, ciphertext.Length);
                }

                if (_isCompressed)
                {
                    fixed (byte* pSrc = dataToDecompress)
                    {
                        if (_method == GameArchive.METHOD_GDEFLATE)
                        {
                            if (!CodecGDeflate.Decompress((void*)destination, (ulong)size, pSrc, (ulong)dataToDecompress.Length, 1))
                                throw new IOException("GDeflate Decompression Failed");
                        }
                        else if (_method == GameArchive.METHOD_ZSTD)
                        {
                            ulong res = CodecZstd.ZSTD_decompress(destination, (ulong)size, (IntPtr)pSrc, (ulong)dataToDecompress.Length);
                            if (CodecZstd.ZSTD_isError(res) != 0) throw new IOException("Zstd Decompression Failed");
                        }
                        else if (_method == GameArchive.METHOD_LZ4)
                        {
                            int res = CodecLZ4.LZ4_decompress_safe((IntPtr)pSrc, destination, dataToDecompress.Length, (int)size);
                            if (res < 0) throw new IOException("LZ4 Decompression Failed");
                        }
                    }
                }
                else
                {
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