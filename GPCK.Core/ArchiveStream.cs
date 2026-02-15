using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace GDeflate.Core
{
    /// <summary>
    /// Stream reader for GameArchive assets.
    /// Supports solid chunks and streaming chunks.
    /// </summary>
    public class ArchiveStream : Stream
    {
        private readonly GameArchive _archive;
        private readonly GameArchive.FileEntry _entry;
        
        private readonly bool _isCompressed;
        private readonly bool _isEncrypted;
        private readonly bool _isStreaming;
        private readonly uint _method;

        private long _position;
        private byte[]? _buffer; // Used for Solid mode
        
        // Streaming State
        private GameArchive.ChunkHeaderEntry[]? _chunks;
        private byte[]? _currentChunkCache;
        private int _currentChunkIndex = -1;
        private long _chunksDataStartOffset;

        // Decryption State
        private AesGcm? _aes;

        public ArchiveStream(GameArchive archive, GameArchive.FileEntry entry)
        {
            _archive = archive;
            _entry = entry;
            _position = 0;
            
            _isCompressed = (entry.Flags & GameArchive.FLAG_IS_COMPRESSED) != 0;
            _isEncrypted = (entry.Flags & GameArchive.FLAG_ENCRYPTED) != 0;
            _isStreaming = (entry.Flags & GameArchive.FLAG_STREAMING) != 0;
            _method = entry.Flags & GameArchive.MASK_METHOD;

            if (_isEncrypted)
            {
                if (_archive.DecryptionKey == null) throw new UnauthorizedAccessException("Encrypted file requires key.");
                _aes = new AesGcm(_archive.DecryptionKey, 16);
            }

            if (_isStreaming && _method == GameArchive.METHOD_ZSTD)
            {
                LoadChunkTable();
            }
        }

        private void LoadChunkTable()
        {
            // Read Table Header from DataOffset
            byte[] countBuffer = new byte[4];
            RandomAccess.Read(_archive.GetFileHandle(), countBuffer, _entry.DataOffset);
            int blockCount = BitConverter.ToInt32(countBuffer);

            int tableSize = blockCount * 8; // 8 bytes per entry
            byte[] tableData = new byte[tableSize];
            RandomAccess.Read(_archive.GetFileHandle(), tableData, _entry.DataOffset + 4);

            _chunks = new GameArchive.ChunkHeaderEntry[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                _chunks[i].CompressedSize = BitConverter.ToUInt32(tableData, i * 8);
                _chunks[i].OriginalSize = BitConverter.ToUInt32(tableData, i * 8 + 4);
            }
            
            _chunksDataStartOffset = _entry.DataOffset + 4 + tableSize;
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
            
            if (_isStreaming && _method == GameArchive.METHOD_ZSTD && _chunks != null)
            {
                return ReadStreaming(buffer);
            }

            // Fallback for Solid (Non-Streaming) or GDeflate blobs
            if (_buffer == null)
            {
                _buffer = new byte[_entry.OriginalSize];
                LoadAndDecompressFull(_buffer);
            }

            int available = (int)(Length - _position);
            int toCopy = Math.Min(buffer.Length, available);
            new Span<byte>(_buffer, (int)_position, toCopy).CopyTo(buffer);
            _position += toCopy;
            return toCopy;
        }

        private int ReadStreaming(Span<byte> buffer)
        {
            int totalRead = 0;
            while (totalRead < buffer.Length && _position < Length)
            {
                // 1. Find which chunk covers _position
                int chunkIdx = 0;
                long pos = 0;
                
                long p = _position;
                if (_chunks![0].OriginalSize == 65536)
                {
                    chunkIdx = (int)(p / 65536);
                    pos = chunkIdx * 65536;
                    if (chunkIdx >= _chunks.Length) chunkIdx = _chunks.Length - 1; 
                }
                else
                {
                    for (int i = 0; i < _chunks.Length; i++)
                    {
                        if (p < pos + _chunks[i].OriginalSize)
                        {
                            chunkIdx = i;
                            break;
                        }
                        pos += _chunks[i].OriginalSize;
                    }
                }

                // 2. Load Chunk if needed
                if (_currentChunkIndex != chunkIdx)
                {
                    LoadChunk(chunkIdx);
                }

                // 3. Copy from chunk
                int offsetInChunk = (int)(_position - pos); 
                int availableInChunk = _currentChunkCache!.Length - offsetInChunk;
                int toCopy = Math.Min(buffer.Length - totalRead, availableInChunk);
                
                new Span<byte>(_currentChunkCache, offsetInChunk, toCopy).CopyTo(buffer.Slice(totalRead));
                
                _position += toCopy;
                totalRead += toCopy;
            }
            return totalRead;
        }

        private void LoadChunk(int index)
        {
            long fileOffset = _chunksDataStartOffset;
            for(int i=0; i<index; i++) fileOffset += _chunks![i].CompressedSize;

            uint cSize = _chunks[index].CompressedSize;
            uint oSize = _chunks[index].OriginalSize;

            byte[] raw = new byte[cSize];
            RandomAccess.Read(_archive.GetFileHandle(), raw, fileOffset);

            // Decrypt
            if (_isEncrypted)
            {
                 if (_aes == null) throw new InvalidOperationException("Encrypted content requires valid key initialization.");
                 
                 int cipherSize = raw.Length - 28;
                 byte[] decrypted = new byte[cipherSize];
                 var nonce = new ReadOnlySpan<byte>(raw, 0, 12);
                 var tag = new ReadOnlySpan<byte>(raw, 12, 16);
                 var cipher = new ReadOnlySpan<byte>(raw, 28, cipherSize);
                 _aes.Decrypt(nonce, cipher, tag, decrypted);
                 raw = decrypted;
            }

            // Decompress
            byte[] decompressed = new byte[oSize];
            unsafe {
                fixed(byte* pIn = raw) fixed(byte* pOut = decompressed) {
                     ulong res = ZstdCodec.ZSTD_decompress((IntPtr)pOut, oSize, (IntPtr)pIn, (ulong)raw.Length);
                     if (ZstdCodec.ZSTD_isError(res) != 0) throw new IOException($"Streaming Decomp Error Chunk {index}");
                }
            }
            _currentChunkCache = decompressed;
            _currentChunkIndex = index;
        }

        private void LoadAndDecompressFull(Span<byte> output)
        {
            byte[] rawData = new byte[_entry.CompressedSize];
            RandomAccess.Read(_archive.GetFileHandle(), rawData, _entry.DataOffset);
            
            Span<byte> processData = rawData;

            if (_isEncrypted)
            {
                if (_aes == null) throw new InvalidOperationException("Encrypted content requires valid key initialization.");

                int cipherSize = rawData.Length - 28;
                byte[] decrypted = new byte[cipherSize];
                var nonce = processData.Slice(0, 12);
                var tag = processData.Slice(12, 16);
                var cipher = processData.Slice(28, cipherSize);
                _aes.Decrypt(nonce, cipher, tag, decrypted);
                processData = decrypted;
            }

            if (_isCompressed)
            {
                if (_method == GameArchive.METHOD_GDEFLATE)
                {
                    unsafe {
                        fixed(byte* pIn = processData) fixed(byte* pOut = output) {
                            if (!GDeflateCodec.Decompress((void*)pOut, (ulong)output.Length, pIn, (ulong)processData.Length, 1))
                                throw new IOException("GDeflate Decompression Failed");
                        }
                    }
                }
                else if (_method == GameArchive.METHOD_ZSTD)
                {
                    unsafe {
                        fixed(byte* pIn = processData) fixed(byte* pOut = output) {
                            ulong res = ZstdCodec.ZSTD_decompress((IntPtr)pOut, (ulong)output.Length, (IntPtr)pIn, (ulong)processData.Length);
                            if (ZstdCodec.ZSTD_isError(res) != 0)
                                throw new IOException("Zstd Decompression Failed");
                        }
                    }
                }
            }
            else
            {
                processData.CopyTo(output);
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
            _aes?.Dispose();
            base.Dispose(disposing);
        }
    }
}