// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.Runtime.Windows;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace Microsoft.Diagnostics.Runtime.CachedReader
{
    internal sealed class CachedReader : MinidumpMemoryReader, IDisposable
    {
#pragma warning disable CA2213 // Disposable fields should be disposed

        // This is disposed at the end of CleanupThread
        private readonly AutoResetEvent _wakeCleanup = new AutoResetEvent(false);

#pragma warning restore CA2213 // Disposable fields should be disposed

        private readonly Dictionary<ulong, CachedPage> _pages = new Dictionary<ulong, CachedPage>();
        private readonly ulong _pageMask;
        private readonly MemoryMappedFile _file;
        private readonly ArrayPool<byte> _pool;
        public long _currSize;
        private volatile bool _done;
        private long _age;

        public long Age => Interlocked.Read(ref _age);

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
            WakeThreshold = (long)(maxSize * 0.95);

            _pool = ArrayPool<byte>.Create((int)PageSize, 8);

            _pageMask = ~((ulong)pageSize - 1);
            for (int i = 0; i < segments.Length; i++)
            {
                CachedPage? prev = null;
                for (ulong start = segments[i].VirtualAddress; start < segments[i].End; start += pageSize)
                {
                    ulong offset = segments[i].FileOffset + start - segments[i].VirtualAddress;
                    int size = Math.Min((int)PageSize, (int)(segments[i].End - start));
                    CachedPage page = new CachedPage(this, offset, start, size);

                    if (prev != null)
                        prev.Next = page;

                    ulong index = start & _pageMask;
                    if (!_pages.ContainsKey(index))
                        _pages.Add(index, page);

                    prev = page;
                }
            }

            Thread thread = new Thread(CleanupThread);
            thread.Start();
        }

        public override void Dispose()
        {
            _done = true;
            _file.Dispose();
            _wakeCleanup.Set();
        }

        private void CleanupThread()
        {
            ulong freeUntil = (ulong)(MaxSize * 0.6);
            long lastHalf = 0;

            // Wake up every 10 seconds regardless of the event triggering just to check _done.
            // This shouldn't be needed but since this thread can keep the cache alive we need
            // to do some defensive coding.
            bool needCleanup = _wakeCleanup.WaitOne(10_000);
            while (!_done)
            {
                if (needCleanup && (ulong)_currSize > freeUntil)
                {
                    long age = Interlocked.Increment(ref _age) - 1;

                    ulong bytesWanted = (ulong)_currSize - freeUntil;
                    uint freed = 0;

                    // First see if there are any really old pages we can expire
                    long oldAge = age >> 1;
                    if (oldAge > lastHalf)
                    {
                        foreach (CachedPage page in _pages.Values)
                        {
                            if (page.Allocated && page.Age < oldAge)
                                freed += (uint)page.PageOut();
                        }

                        lastHalf = oldAge + (oldAge >> 1);
                    }

                    foreach (CachedPage page in _pages.Values)
                    {
                        if (freed >= bytesWanted)
                            break;

                        if (page.Allocated && page.Age < age)
                            freed += (uint)page.PageOut();
                    }

                    if (freed < bytesWanted)
                    {
                        // If playing nice didn't work, just clear pages until we are lower.
                        foreach (CachedPage page in _pages.Values)
                        {
                            if (page.Allocated)
                            {
                                freed += (uint)page.PageOut();

                                if (freed >= bytesWanted)
                                    break;
                            }
                        }
                    }
                }

                needCleanup = _wakeCleanup.WaitOne(10_000);
            }

            _wakeCleanup.Dispose();
        }

        public unsafe override int ReadFromRva(ulong rva, Span<byte> buffer)
        {
            using MemoryMappedViewAccessor view = _file.CreateViewAccessor((long)rva, buffer.Length, MemoryMappedFileAccess.Read);

            byte* pViewLoc = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pViewLoc);
                if (pViewLoc == null)
                    return 0;

                pViewLoc += view.PointerOffset;

                Span<byte> mapped = new Span<byte>(pViewLoc, buffer.Length);
                mapped.CopyTo(buffer);

                return buffer.Length;
            }
            finally
            {
                if (pViewLoc != null)
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        public override int Read(ulong baseAddress, Span<byte> bytes)
        {
            int bytesRead = 0;

            ulong address = baseAddress;
            while (bytesRead < bytes.Length)
            {
                if (!_pages.TryGetValue(address & _pageMask, out CachedPage? page))
                    break;

                ulong curr = address;
                while (page != null)
                {
                    Span<byte> slice = bytes.Slice(bytesRead, bytes.Length - bytesRead);
                    int read = page.Read(address, slice);
                    if (read == 0)
                        break;

                    bytesRead += read;
                    address += (uint)read;

                    if (bytesRead == bytes.Length)
                        break;

                    page = page.Next;
                    if (page != null)
                    {

                    }
                }

                if (curr == address)
                    break;
            }


            return bytesRead;
        }

        internal unsafe byte[] ReadPage(ulong fileOffset, int size)
        {
            byte[] bytes = _pool.Rent(size);

            byte* pViewLoc = null;
            using MemoryMappedViewAccessor view = _file.CreateViewAccessor((long)fileOffset, size, MemoryMappedFileAccess.Read);
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pViewLoc);
                if (pViewLoc == null)
                    return Array.Empty<byte>();

                pViewLoc += view.PointerOffset;

                Span<byte> mapped = new Span<byte>(pViewLoc, size);
                mapped.CopyTo(bytes);
            }
            finally
            {
                if (pViewLoc != null)
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            // Note we add bytes.Length here because we want to track memory used which is the length of the array, not the size
            // of the segment requeseted.
            long newSize = Interlocked.Add(ref _currSize, bytes.Length);
            if (newSize >= WakeThreshold)
                _wakeCleanup.Set();

            return bytes;
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

        public bool Allocated => _page != null;
        public long Age { get; private set; }

        public ulong Offset { get; }
        public ulong Start { get; }
        public int Size { get; }
        
        public CachedPage? Next { get; set; }

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
            if (address < Start || address > Start + (uint)Size)
                return 0;
                
            if (_page != null)
            {
                _rwLock.EnterReadLock();
                try
                {
                    if (_page != null)
                        return ReadLocked(address, buffer);
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
                        _page = _parent.ReadPage(Offset, Size);
                    }
                    finally
                    {
                        _rwLock.ExitWriteLock();
                    }
                }

                return ReadLocked(address, buffer);
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

            Age = _parent.Age;
            return page.Length;
        }
    }
}
