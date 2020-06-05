// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.Runtime.Windows;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace Microsoft.Diagnostics.Runtime.CachedReader
{
    internal sealed class CachedReader : IDisposable
    {
        private readonly AutoResetEvent _wakeCleanup = new AutoResetEvent(false);
        private readonly Dictionary<ulong, CachedPage> _pages = new Dictionary<ulong, CachedPage>();
        private readonly uint _pageMask;
        private readonly MemoryMappedFile _file;
        private readonly ArrayPool<byte> _pool;
        public long _currSize;
        private volatile bool _done;

        public uint PageSize { get; }
        public ulong MaxSize { get; }
        public long WakeThreshold { get; }

        public CachedReader(MemoryMappedFile file, ImmutableArray<MinidumpSegment> segments, uint pageSize, ulong maxSize)
        {
            _file = file;
            if ((pageSize & (pageSize - 1)) != 0)
                throw new ArgumentException($"{nameof(pageSize)} must be a power of 2.");

            PageSize = pageSize;
            MaxSize = maxSize;
            WakeThreshold = (long)(maxSize * 0.9);

            _pool = ArrayPool<byte>.Create((int)PageSize, 8);

            _pageMask = ~(pageSize - 1);
            for (int i = 0; i < segments.Length; i++)
            {
                for (ulong start = segments[i].VirtualAddress; start < segments[i].End; start += pageSize)
                {
                    ulong offset = (start - segments[i].VirtualAddress) + segments[i].FileOffset;
                    int size = Math.Min((int)PageSize, (int)(segments[i].End - start));
                    _pages.Add(start & _pageMask, new CachedPage(this, offset, start, size));
                }

                // todo, what if two segments start in the same page range?
            }

            Thread thread = new Thread(CleanupThread);
            thread.Start();
        }

        public void Dispose()
        {
            _done = true;
            _file.Dispose();
            _wakeCleanup.Dispose();
            _wakeCleanup.Set();
        }

        private void CleanupThread()
        {
            while (_wakeCleanup.WaitOne())
            {
                // todo:  loop through _pages and call CachedPage.PageOut until _currSize is much less than WakeThreshold
            }
        }

        public int Read(ulong baseAddress, Span<byte> bytes)
        {
            int bytesRead = 0;

            ulong address = baseAddress;
            while (bytesRead < bytes.Length)
            {
                if (!_pages.TryGetValue(address & _pageMask, out CachedPage? page))
                    return bytesRead;

                Span<byte> slice = bytes.Slice(bytesRead, bytes.Length - bytesRead);
                int read = page.Read(address, slice);
                if (read == 0)
                    return bytesRead;

                bytesRead += read;
            }

            return bytesRead;
        }

        internal byte[] Read(ulong fileOffset, int size)
        {
            byte[] bytes = _pool.Rent(size);

            int read;
            try
            {
                using MemoryMappedViewAccessor view = _file.CreateViewAccessor((long)fileOffset, size, MemoryMappedFileAccess.Read);

                read = view.ReadArray(0, bytes, 0, size);
                if (read == size)
                {
                    long newSize = Interlocked.Add(ref _currSize, size);
                    if (newSize >= WakeThreshold)
                        _wakeCleanup.Set();

                    return bytes;
                }
            }
            catch
            {
                read = 0;
            }

            if (read == 0)
                return Array.Empty<byte>();


            byte[] result = new byte[read];
            Span<byte> span = new Span<byte>(bytes, 0, read);
            span.CopyTo(result);

            {
                long newSize = Interlocked.Add(ref _currSize, read);
                if (newSize >= WakeThreshold)
                    _wakeCleanup.Set();
            }

            _pool.Return(bytes);
            return result;
        }

        internal void Return(byte[] page)
        {
            if (page.Length == PageSize)
                _pool.Return(page);

            Interlocked.Add(ref _currSize, -page.Length);
        }
    }

    internal sealed class CachedPage : IDisposable
    {
        private readonly CachedReader _parent;
        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim();
        private volatile byte[]? _page;

        private long _lastAccessTickCount;

        public long LastAccess => _lastAccessTickCount;

        public ulong Offset { get; }
        public ulong Start { get; }
        public int Size { get; }

        public CachedPage(CachedReader parent, ulong offset, ulong address, int size)
        {
            _parent = parent;
            Offset = offset;
            Start = address;
            Size = size;
        }

        public void Dispose() => _rwLock.Dispose();

        public int PageOut()
        {
            if (_page is null)
                return 0;

            byte[]? page = null;

            _rwLock.EnterWriteLock();
            try
            {
                if (_page is null)
                    return 0;

                page = _page;
                _page = null;
            }
            finally
            {
                _rwLock.ExitWriteLock();
            }

            _parent.Return(page);
            return page.Length;
        }

        public int Read(ulong address, Span<byte> buffer)
        {
            DebugOnly.Assert(Start <= address && address + (uint)buffer.Length < Start + (uint)Size);

            if (_page != null)
            {
                _rwLock.EnterReadLock();
                try
                {
                    if (_page != null)
                        return ReadLocked(Offset, buffer);
                }
                finally
                {
                    _rwLock.ExitReadLock();
                }
            }

            _rwLock.EnterUpgradeableReadLock();
            try
            {
                if (_page is null)
                {
                    _rwLock.EnterWriteLock();

                    try
                    {
                        _page = _parent.Read(Start, Size);
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }
                }

                return ReadLocked(Offset, buffer);
            }
            finally
            {
                _rwLock.ExitUpgradeableReadLock();
            }
        }

        private int ReadLocked(ulong address, Span<byte> buffer)
        {
            DebugOnly.Assert(_rwLock.IsReadLockHeld || _rwLock.IsUpgradeableReadLockHeld);
            DebugOnly.Assert(_page != null);

            Span<byte> page = _page!;

            int offset = (int)(address - Start);
            int len = Math.Min(buffer.Length, page.Length - offset);

            page = page.Slice(offset, len);
            page.CopyTo(buffer);

            UpdateLastAccessTickCount();
            return page.Length;
        }

        private void UpdateLastAccessTickCount()
        {
            long originalTickCountValue = Interlocked.Read(ref _lastAccessTickCount);

            while (true)
            {
                CacheNativeMethods.Util.QueryPerformanceCounter(out long currentTickCount);
                if (Interlocked.CompareExchange(ref _lastAccessTickCount, currentTickCount, originalTickCountValue) == originalTickCountValue)
                {
                    break;
                }

                originalTickCountValue = Interlocked.Read(ref _lastAccessTickCount);
            }
        }
    }
}
