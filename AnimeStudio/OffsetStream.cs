using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnimeStudio
{
    public class OffsetStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long? _length;
        private long _offset;
        private long _position;

        public override bool CanRead => _baseStream.CanRead;
        public override bool CanSeek => _baseStream.CanSeek;
        public override bool CanWrite => false;

        public long Offset
        {
            get => _offset;
            set
            {
                if (value < 0 || value > _baseStream.Length)
                {
                    throw new IOException($"{nameof(Offset)} is out of stream bound");
                }
                _offset = value;
                _position = 0;
            }
        }
        public long AbsolutePosition => _offset + _position;
        public long Remaining => Length - Position;

        public override long Length => _length ?? _baseStream.Length - _offset;
        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public OffsetStream(Stream stream, long offset)
        {
            _baseStream = stream;

            Offset = offset;
        }

        public OffsetStream(Stream stream, long offset, long length)
        {
            if (length < 0)
            {
                throw new IOException($"{nameof(length)} is out of stream bound");
            }
            if (offset < 0 || offset + length > stream.Length)
            {
                throw new IOException($"{nameof(Offset)} is out of stream bound");
            }

            _baseStream = stream;
            _length = length;
            Offset = offset;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => offset + _position,
                SeekOrigin.End => offset + Length,
                _ => throw new NotSupportedException()
            };
            if (target < 0 || target > Length)
            {
                throw new IOException("Unable to seek beyond stream bound");
            }

            _position = target;
            return _position;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = Remaining;
            if (remaining <= 0)
            {
                return 0;
            }
            lock (_baseStream)
            {
                _baseStream.Position = _offset + _position;
                var read = _baseStream.Read(buffer, offset, (int)Math.Min(count, remaining));
                _position += read;
                return read;
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Flush() => throw new NotImplementedException();
        public IEnumerable<long> GetOffsets(string path)
        {
            if (AssetsHelper.TryGet(path, out var offsets))
            {
                foreach (var offset in offsets)
                {
                    Offset = offset;
                    yield return offset;
                }
            }
            else
            {
                while (Remaining > 0)
                {
                    Offset = AbsolutePosition;
                    yield return AbsolutePosition;
                    if (Offset == AbsolutePosition)
                    {
                        break;
                    }
                }
            }
        }
    }
}
