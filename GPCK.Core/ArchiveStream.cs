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
        private readonly bool _isChunked;
        private readonly uint _method;

        private long _position;
        private AesGcm? _aes;

        private byte[]? _currentChunkData;
        private int _currentChunkIndex = -1;

        private GameArchive.ChunkHeaderEntry[]? _chunkTable;
        private long _dataStartOffset;

        public ArchiveStream(GameArchive archive, GameArchive.FileEntry entry)
        {
            _archive = archive;
            _entry = entry;
            _position = 0;

            _isCompressed = (entry.Flags & GameArchive.FLAG_IS_COMPRESSED) != 0;
            _isEncrypted = (entry.Flags & GameArchive.FLAG_ENCRYPTED) != 0;
            _isChunked = (entry.Flags & GameArchive.FLAG_STREAMING) != 0;
            _method = entry.Flags & GameArchive.MASK_METHOD;

            if (_isEncrypted && _archive.DecryptionKey != null)
            {
                _aes = new AesGcm(_archive.DecryptionKey, 16);
            }

            if (_isChunked)
            {
                InitializeChunkTable();
            }
        }

        private void InitializeChunkTable()
        {
            byte[] header = new byte[4];
            RandomAccess.Read(_archive.GetFileHandle(), header, _entry.DataOffset);
            int count = BitConverter.ToInt32(header, 0);

            _chunkTable = new GameArchive.ChunkHeaderEntry[count];
            byte[] tableBuffer = new byte[count * 8];
            RandomAccess.Read(_archive.GetFileHandle(), tableBuffer, _entry.DataOffset + 4);

            for (int i = 0; i < count; i++)
            {
                _chunkTable[i] = new GameArchive.ChunkHeaderEntry {
                    CompressedSize = BitConverter.ToUInt32(tableBuffer, i * 8),
                    OriginalSize = BitConverter.ToUInt32(tableBuffer, i * 8 + 4)
                };
            }
            _dataStartOffset = _entry.DataOffset + 4 + (count * 8);
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
            int totalRead = 0;
            int toRead = Math.Min(buffer.Length, (int)(Length - _position));

            if (!_isChunked)
            {
                // Simple non-chunked fallback (rare in GPCK)
                byte[] raw = ArrayPool<byte>.Shared.Rent((int)_entry.CompressedSize);
                try {
                    RandomAccess.Read(_archive.GetFileHandle(), raw.AsSpan(0, (int)_entry.CompressedSize), _entry.DataOffset);
                    byte[] decoded = new byte[_entry.OriginalSize];
                    ProcessBlock(raw.AsSpan(0, (int)_entry.CompressedSize), decoded, _entry.OriginalSize);
                    int slice = Math.Min(toRead, (int)(_entry.OriginalSize - _position));
                    decoded.AsSpan((int)_position, slice).CopyTo(buffer);
                    _position += slice;
                    return slice;
                } finally { ArrayPool<byte>.Shared.Return(raw); }
            }

            while (totalRead < toRead)
            {
                int chunkIdx = GetChunkIndexForPosition(_position, out long offsetInChunk);
                LoadChunk(chunkIdx);

                int availableInChunk = (int)(_chunkTable![chunkIdx].OriginalSize - offsetInChunk);
                int copyCount = Math.Min(toRead - totalRead, availableInChunk);

                _currentChunkData.AsSpan((int)offsetInChunk, copyCount).CopyTo(buffer.Slice(totalRead));

                _position += copyCount;
                totalRead += copyCount;
            }

            return totalRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return await ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= Length) return 0;
            int totalRead = 0;
            int toRead = Math.Min(buffer.Length, (int)(Length - _position));

            if (!_isChunked)
            {
                byte[] raw = ArrayPool<byte>.Shared.Rent((int)_entry.CompressedSize);
                try {
                    await RandomAccess.ReadAsync(_archive.GetFileHandle(), raw.AsMemory(0, (int)_entry.CompressedSize), _entry.DataOffset, cancellationToken).ConfigureAwait(false);
                    byte[] decoded = new byte[_entry.OriginalSize];
                    ProcessBlock(raw.AsSpan(0, (int)_entry.CompressedSize), decoded, _entry.OriginalSize);
                    int slice = Math.Min(toRead, (int)(_entry.OriginalSize - _position));
                    decoded.AsSpan((int)_position, slice).CopyTo(buffer.Span);
                    _position += slice;
                    return slice;
                } finally { ArrayPool<byte>.Shared.Return(raw); }
            }

            while (totalRead < toRead)
            {
                int chunkIdx = GetChunkIndexForPosition(_position, out long offsetInChunk);
                await LoadChunkAsync(chunkIdx, cancellationToken).ConfigureAwait(false);

                int availableInChunk = (int)(_chunkTable![chunkIdx].OriginalSize - offsetInChunk);
                int copyCount = Math.Min(toRead - totalRead, availableInChunk);

                _currentChunkData.AsSpan((int)offsetInChunk, copyCount).CopyTo(buffer.Span.Slice(totalRead));

                _position += copyCount;
                totalRead += copyCount;
            }

            return totalRead;
        }

        private int GetChunkIndexForPosition(long pos, out long offsetInChunk)
        {
            if (!_isChunked || _chunkTable == null) { offsetInChunk = pos; return 0; }

            long accumulated = 0;
            for (int i = 0; i < _chunkTable.Length; i++)
            {
                if (pos < accumulated + _chunkTable[i].OriginalSize)
                {
                    offsetInChunk = pos - accumulated;
                    return i;
                }
                accumulated += _chunkTable[i].OriginalSize;
            }
            offsetInChunk = 0;
            return _chunkTable.Length - 1;
        }

        private void LoadChunk(int index)
        {
            if (_currentChunkIndex == index && _currentChunkData != null) return;

            long physicalOffset = _dataStartOffset;
            for (int i = 0; i < index; i++) physicalOffset += _chunkTable![i].CompressedSize;

            uint compSize = _chunkTable![index].CompressedSize;
            uint origSize = _chunkTable![index].OriginalSize;

            byte[] compBuffer = ArrayPool<byte>.Shared.Rent((int)compSize);
            try
            {
                RandomAccess.Read(_archive.GetFileHandle(), compBuffer.AsSpan(0, (int)compSize), physicalOffset);

                if (_currentChunkData == null || _currentChunkData.Length < origSize)
                    _currentChunkData = new byte[origSize];

                ProcessBlock(compBuffer.AsSpan(0, (int)compSize), _currentChunkData, origSize);
                _currentChunkIndex = index;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compBuffer);
            }
        }

        private async ValueTask LoadChunkAsync(int index, CancellationToken ct)
        {
            if (_currentChunkIndex == index && _currentChunkData != null) return;

            long physicalOffset = _dataStartOffset;
            for (int i = 0; i < index; i++) physicalOffset += _chunkTable![i].CompressedSize;

            uint compSize = _chunkTable![index].CompressedSize;
            uint origSize = _chunkTable![index].OriginalSize;

            byte[] compBuffer = ArrayPool<byte>.Shared.Rent((int)compSize);
            try
            {
                await RandomAccess.ReadAsync(_archive.GetFileHandle(), compBuffer.AsMemory(0, (int)compSize), physicalOffset, ct).ConfigureAwait(false);

                if (_currentChunkData == null || _currentChunkData.Length < origSize)
                    _currentChunkData = new byte[origSize];

                ProcessBlock(compBuffer.AsSpan(0, (int)compSize), _currentChunkData, origSize);
                _currentChunkIndex = index;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compBuffer);
            }
        }

        private unsafe void ProcessBlock(ReadOnlySpan<byte> source, byte[] destination, uint targetSize)
        {
            ReadOnlySpan<byte> dataToDecompress = source;

            if (_isEncrypted && _aes != null)
            {
                var nonce = source.Slice(0, 12);
                var tag = source.Slice(12, 16);
                var ciphertext = source.Slice(28);
                byte[] dec = ArrayPool<byte>.Shared.Rent(ciphertext.Length);
                try {
                    _aes.Decrypt(nonce, ciphertext, tag, dec.AsSpan(0, ciphertext.Length));
                    dataToDecompress = dec.AsSpan(0, ciphertext.Length);
                    DecompressInternal(dataToDecompress, destination, targetSize);
                } finally {
                    ArrayPool<byte>.Shared.Return(dec);
                }
            }
            else
            {
                DecompressInternal(dataToDecompress, destination, targetSize);
            }
        }

        private unsafe void DecompressInternal(ReadOnlySpan<byte> source, byte[] destination, uint targetSize)
        {
            if (!_isCompressed)
            {
                source.CopyTo(destination);
                return;
            }

            fixed (byte* pSrc = source)
            fixed (byte* pDst = destination)
            {
                if (_method == GameArchive.METHOD_GDEFLATE)
                {
                    if (!CodecGDeflate.Decompress(pDst, targetSize, pSrc, (ulong)source.Length, 1))
                        throw new IOException("GDeflate Error");
                }
                else if (_method == GameArchive.METHOD_ZSTD)
                {
                    ulong res = CodecZstd.ZSTD_decompress((IntPtr)pDst, targetSize, (IntPtr)pSrc, (ulong)source.Length);
                    if (CodecZstd.ZSTD_isError(res) != 0) throw new IOException("Zstd Error");
                }
                else if (_method == GameArchive.METHOD_LZ4)
                {
                    int res = CodecLZ4.LZ4_decompress_safe((IntPtr)pSrc, (IntPtr)pDst, source.Length, (int)targetSize);
                    if (res < 0) throw new IOException("LZ4 Error");
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
