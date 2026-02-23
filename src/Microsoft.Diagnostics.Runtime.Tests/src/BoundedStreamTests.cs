// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Diagnostics.Runtime.Implementation;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class BoundedStreamTests
    {
        [Fact]
        public void ReadWithinLimitSucceeds()
        {
            byte[] data = new byte[100];
            new Random(42).NextBytes(data);

            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 200);

            byte[] buffer = new byte[100];
            int read = bounded.Read(buffer, 0, buffer.Length);

            Assert.Equal(100, read);
            Assert.Equal(data, buffer);
        }

        [Fact]
        public void ReadExactlyAtLimitSucceeds()
        {
            byte[] data = new byte[100];
            new Random(42).NextBytes(data);

            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 100);

            byte[] buffer = new byte[100];
            int read = bounded.Read(buffer, 0, buffer.Length);

            Assert.Equal(100, read);
            Assert.Equal(data, buffer);
        }

        [Fact]
        public void ReadExceedingLimitCapsAtMaxBytes()
        {
            byte[] data = new byte[200];
            new Random(42).NextBytes(data);

            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 100);

            byte[] buffer = new byte[200];
            int read = bounded.Read(buffer, 0, buffer.Length);

            Assert.Equal(100, read);
            Assert.Equal(data.AsSpan(0, 100).ToArray(), buffer.AsSpan(0, 100).ToArray());
        }

        [Fact]
        public void MultipleReadsCappedAtMaxBytes()
        {
            byte[] data = new byte[200];
            new Random(42).NextBytes(data);

            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 150);

            byte[] buffer = new byte[100];

            // First read: 100 bytes, within limit
            int read1 = bounded.Read(buffer, 0, buffer.Length);
            Assert.Equal(100, read1);

            // Second read: only 50 bytes remaining within limit
            int read2 = bounded.Read(buffer, 0, buffer.Length);
            Assert.Equal(50, read2);

            // Third read: at limit, returns 0
            int read3 = bounded.Read(buffer, 0, buffer.Length);
            Assert.Equal(0, read3);
        }

        [Fact]
        public void CopyToWithinLimitSucceeds()
        {
            byte[] data = new byte[100];
            new Random(42).NextBytes(data);

            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 200);
            using MemoryStream output = new();

            bounded.CopyTo(output);

            Assert.Equal(data, output.ToArray());
        }

        [Fact]
        public void CopyToExceedingLimitCapsOutput()
        {
            byte[] data = new byte[200];
            new Random(42).NextBytes(data);

            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 100);
            using MemoryStream output = new();

            bounded.CopyTo(output);

            Assert.Equal(100, output.Length);
            Assert.Equal(data.AsSpan(0, 100).ToArray(), output.ToArray());
        }

        [Fact]
        public void ConstructorRejectsNullStream()
        {
            Assert.Throws<ArgumentNullException>(() => new BoundedStream(null!, 100));
        }

        [Fact]
        public void ConstructorRejectsZeroMaxBytes()
        {
            using MemoryStream inner = new();
            Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedStream(inner, 0));
        }

        [Fact]
        public void ConstructorRejectsNegativeMaxBytes()
        {
            using MemoryStream inner = new();
            Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedStream(inner, -1));
        }

        [Fact]
        public void PositionReportsTotalBytesRead()
        {
            byte[] data = new byte[100];
            using MemoryStream inner = new(data);
            using BoundedStream bounded = new(inner, 200);

            byte[] buffer = new byte[50];
            _ = bounded.Read(buffer, 0, 30);
            Assert.Equal(30, bounded.Position);

            _ = bounded.Read(buffer, 0, 20);
            Assert.Equal(50, bounded.Position);
        }

        [Fact]
        public void WriteThrowsNotSupported()
        {
            using MemoryStream inner = new();
            using BoundedStream bounded = new(inner, 100);

            Assert.Throws<NotSupportedException>(() => bounded.Write(new byte[1], 0, 1));
        }
    }
}
