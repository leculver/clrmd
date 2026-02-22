// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Diagnostics.Runtime.Implementation;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class FileSymbolCacheStoreTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly FileSymbolCache _cache;

        public FileSymbolCacheStoreTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "clrmd_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _cache = new FileSymbolCache(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        [Fact]
        public void StoreWithinMaxSizeSucceeds()
        {
            byte[] data = new byte[100];
            new Random(42).NextBytes(data);

            using MemoryStream stream = new(data);
            string result = _cache.Store(stream, "test/within.bin", maxSize: 200);

            Assert.True(File.Exists(result));
            Assert.Equal(data, File.ReadAllBytes(result));
        }

        [Fact]
        public void StoreExactlyAtMaxSizeSucceeds()
        {
            byte[] data = new byte[100];
            new Random(42).NextBytes(data);

            using MemoryStream stream = new(data);
            string result = _cache.Store(stream, "test/exact.bin", maxSize: 100);

            Assert.True(File.Exists(result));
            Assert.Equal(data, File.ReadAllBytes(result));
        }

        [Fact]
        public void StoreExceedingMaxSizeThrowsAndDeletesPartialFile()
        {
            byte[] data = new byte[200];
            new Random(42).NextBytes(data);

            string expectedPath = Path.Combine(_tempDir, "test", "oversized.bin");

            using MemoryStream stream = new(data);
            Assert.Throws<InvalidOperationException>(() => _cache.Store(stream, "test/oversized.bin", maxSize: 100));

            // Partial file should be cleaned up
            Assert.False(File.Exists(expectedPath));
        }

        [Fact]
        public void StoreWithZeroMaxSizeDoesNotEnforceLimit()
        {
            byte[] data = new byte[500];
            new Random(42).NextBytes(data);

            using MemoryStream stream = new(data);
            string result = _cache.Store(stream, "test/nolimit.bin", maxSize: 0);

            Assert.True(File.Exists(result));
            Assert.Equal(data, File.ReadAllBytes(result));
        }

        [Fact]
        public void StoreWithoutMaxSizeDoesNotEnforceLimit()
        {
            byte[] data = new byte[500];
            new Random(42).NextBytes(data);

            using MemoryStream stream = new(data);
            string result = _cache.Store(stream, "test/default.bin");

            Assert.True(File.Exists(result));
            Assert.Equal(data, File.ReadAllBytes(result));
        }
    }
}
