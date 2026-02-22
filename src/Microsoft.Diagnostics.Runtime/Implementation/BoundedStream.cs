// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

namespace Microsoft.Diagnostics.Runtime.Implementation
{
    /// <summary>
    /// A read-only stream wrapper that enforces a maximum number of bytes read.
    /// Throws <see cref="InvalidOperationException"/> if the limit is exceeded.
    /// </summary>
    internal sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _totalBytesRead;

        public BoundedStream(Stream inner, long maxBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));

            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _totalBytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = _inner.Read(buffer, offset, count);
            _totalBytesRead += bytesRead;

            if (_totalBytesRead > _maxBytes)
                throw new InvalidOperationException($"Download exceeded maximum allowed size of {_maxBytes:N0} bytes.");

            return bytesRead;
        }

#if NET
        public override int Read(Span<byte> buffer)
        {
            int bytesRead = _inner.Read(buffer);
            _totalBytesRead += bytesRead;

            if (_totalBytesRead > _maxBytes)
                throw new InvalidOperationException($"Download exceeded maximum allowed size of {_maxBytes:N0} bytes.");

            return bytesRead;
        }
#endif

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();

            base.Dispose(disposing);
        }
    }
}
