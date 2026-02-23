// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

namespace Microsoft.Diagnostics.Runtime.Implementation
{
    /// <summary>
    /// A read-only stream wrapper that caps reads at a maximum number of bytes.
    /// Returns EOF (0) once the limit is reached.
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
            long remaining = _maxBytes - _totalBytesRead;
            if (remaining <= 0)
                return 0;

            if (count > remaining)
                count = (int)remaining;

            int bytesRead = _inner.Read(buffer, offset, count);
            _totalBytesRead += bytesRead;
            return bytesRead;
        }

#if NET
        public override int Read(Span<byte> buffer)
        {
            long remaining = _maxBytes - _totalBytesRead;
            if (remaining <= 0)
                return 0;

            if (buffer.Length > remaining)
                buffer = buffer.Slice(0, (int)remaining);

            int bytesRead = _inner.Read(buffer);
            _totalBytesRead += bytesRead;
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
