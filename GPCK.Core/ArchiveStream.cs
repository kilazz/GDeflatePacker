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
        private readonly bool _isStreaming;
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
            _isStreaming = (entry.Flags & GameArchive.FLAG_STREAMING) != 0;
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
            if (_cpuCache == null) FillCache();

            int available = (int)(Length - _position);
            int toCopy = Math.Min(buffer.Length, available);
            if (toCopy <= 0) return 0;

            new Span<byte>(_cpuCache, (int)_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
             if (_cpuCache == null) await Task.Run(FillCache, cancellationToken).ConfigureAwait(false);
             return Read(new Span<byte>(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_cpuCache == null)
            {
                 return new ValueTask<int>(Task.Run(() => { FillCache(); return Read(buffer.Span); }, cancellationToken));
            }
            return new ValueTask<int>(Read(buffer.Span));
        }

        private void FillCache()
        {
            if (_cpuCache != null) return;
            _cpuCache = new byte[_entry.OriginalSize];
            unsafe {
                fixed (byte* ptr = _cpuCache) {
                    ReadToNative((IntPtr)ptr, _entry.OriginalSize);
                }
            }
        }

        public unsafe void ReadToNative(IntPtr destination, long size)
        {
            if (size == 0) return;

            if (_isStreaming) {
                ReadStreaming(destination, size);
                return;
            }

            int compressedSize = (int)_entry.CompressedSize;
            byte[] compressedBuffer = ArrayPool<byte>.Shared.Rent(compressedSize);
            try {
                RandomAccess.Read(_archive.GetFileHandle(), compressedBuffer.AsSpan(0, compressedSize), _entry.DataOffset);
                ReadOnlySpan<byte> dataToDecompress = compressedBuffer.AsSpan(0, compressedSize);

                if (_isEncrypted && _aes != null) {
                    var nonce = dataToDecompress.Slice(0, 12);
                    var tag = dataToDecompress.Slice(12, 16);
                    var ciphertext = dataToDecompress.Slice(28);
                    byte[] decryptBuffer = ArrayPool<byte>.Shared.Rent(ciphertext.Length);
                    _aes.Decrypt(nonce, ciphertext, tag, decryptBuffer.AsSpan(0, ciphertext.Length));
                    dataToDecompress = decryptBuffer.AsSpan(0, ciphertext.Length);
                    // We must use a copy since decryptBuffer is rented and ciphertext will be processed
                    byte[] finalData = new byte[dataToDecompress.Length];
                    dataToDecompress.CopyTo(finalData);
                    ProcessDecompression(destination, size, finalData);
                    ArrayPool<byte>.Shared.Return(decryptBuffer);
                } else {
                    ProcessDecompression(destination, size, dataToDecompress.ToArray());
                }
            } finally {
                ArrayPool<byte>.Shared.Return(compressedBuffer);
            }
        }

        private unsafe void ReadStreaming(IntPtr destination, long totalOriginalSize)
        {
            byte[] headerBuffer = new byte[4];
            RandomAccess.Read(_archive.GetFileHandle(), headerBuffer, _entry.DataOffset);
            int blockCount = BitConverter.ToInt32(headerBuffer, 0);

            int tableSize = blockCount * 8;
            byte[] tableBuffer = new byte[tableSize];
            RandomAccess.Read(_archive.GetFileHandle(), tableBuffer, _entry.DataOffset + 4);

            long currentDataOffset = _entry.DataOffset + 4 + tableSize;
            long currentDestOffset = 0;

            for (int i = 0; i < blockCount; i++) {
                uint compSize = BitConverter.ToUInt32(tableBuffer, i * 8);
                uint origSize = BitConverter.ToUInt32(tableBuffer, i * 8 + 4);

                byte[] blockBuffer = new byte[compSize];
                RandomAccess.Read(_archive.GetFileHandle(), blockBuffer, currentDataOffset);
                currentDataOffset += compSize;

                ReadOnlySpan<byte> blockSpan = blockBuffer;
                if (_isEncrypted && _aes != null) {
                    byte[] dec = new byte[compSize - 28];
                    _aes.Decrypt(blockSpan.Slice(0, 12), blockSpan.Slice(28), blockSpan.Slice(12, 16), dec);
                    blockSpan = dec;
                }

                IntPtr blockDest = (IntPtr)((byte*)destination + currentDestOffset);
                ProcessDecompression(blockDest, origSize, blockSpan.ToArray());
                currentDestOffset += origSize;
            }
        }

        private unsafe void ProcessDecompression(IntPtr destination, long targetSize, byte[] source)
        {
            if (!_isCompressed) {
                fixed (byte* pSrc = source) {
                    Buffer.MemoryCopy(pSrc, (void*)destination, targetSize, Math.Min(targetSize, source.Length));
                }
                return;
            }

            fixed (byte* pSrc = source) {
                if (_method == GameArchive.METHOD_GDEFLATE) {
                    if (!CodecGDeflate.Decompress((void*)destination, (ulong)targetSize, pSrc, (ulong)source.Length, 1))
                        throw new IOException("GDeflate Decompression Failed");
                } else if (_method == GameArchive.METHOD_ZSTD) {
                    ulong res = CodecZstd.ZSTD_decompress(destination, (ulong)targetSize, (IntPtr)pSrc, (ulong)source.Length);
                    if (CodecZstd.ZSTD_isError(res) != 0) throw new IOException("Zstd Decompression Failed");
                } else if (_method == GameArchive.METHOD_LZ4) {
                    int res = CodecLZ4.LZ4_decompress_safe((IntPtr)pSrc, destination, source.Length, (int)targetSize);
                    if (res < 0) throw new IOException("LZ4 Decompression Failed");
                } else {
                    Buffer.MemoryCopy(pSrc, (void*)destination, targetSize, Math.Min(targetSize, source.Length));
                }
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentException("Invalid Origin")
            };
            _position = Math.Clamp(target, 0, Length);
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _aes?.Dispose();
            base.Dispose(disposing);
        }
    }
}
