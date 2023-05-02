// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace Microsoft.Diagnostics.Runtime.DataReaders
{
    internal class ReadCache : IDataReader
    {
        private readonly IDataReader _reader;
        private readonly LruCache _cache;

        public ReadCache(IDataReader reader, int pageCount, int pageSize)
        {
            _reader = reader;
            _cache = new(pageCount, pageSize, _reader.IsThreadSafe);
        }

        public string DisplayName => _reader.DisplayName;

        public bool IsThreadSafe => _reader.IsThreadSafe;

        public OSPlatform TargetPlatform => _reader.TargetPlatform;

        public Architecture Architecture => _reader.Architecture;

        public int ProcessId => _reader.ProcessId;

        public int PointerSize => _reader.PointerSize;

        public IEnumerable<ModuleInfo> EnumerateModules() => _reader.EnumerateModules();
        public void FlushCachedData() => _reader.FlushCachedData();
        public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context) => _reader.GetThreadContext(threadID, contextFlags, context);

        public int Read(ulong address, Span<byte> buffer)
        {
            ulong currAddress = address;
            int read = 0;

            int readCount = 0;
            while (read < buffer.Length)
            {
                readCount++;
                LruCache.Entry entry = _cache.GetOrCreateCacheForAddress(currAddress);
                byte[] entryBuffer = entry.GetOrRead(_reader);
                if (entryBuffer.Length == 0)
                    break;

                Debug.Assert(entry.BaseAddress <= currAddress);
                Debug.Assert(currAddress - entry.BaseAddress < int.MaxValue);

                int diff = (int)(currAddress - entry.BaseAddress);
                Span<byte> overlap = entryBuffer.AsSpan(diff, Math.Min(entryBuffer.Length - diff, buffer.Length - read));
                Debug.Assert(overlap.Length > 0);

                Span<byte> output = buffer.Slice(read);
                overlap.CopyTo(output);

                read += overlap.Length;
                currAddress += (uint)overlap.Length;
            }

            if (readCount > 1)
                _cache.ReportMultiRead();

            return read;
        }

        public bool Read<T>(ulong address, out T value) where T : unmanaged
        {
            Span<byte> bytes = stackalloc byte[64];

            int size = Unsafe.SizeOf<T>();
            if (bytes.Length < size)
            {
                bytes = bytes.Slice(0, size);
                int read = _reader.Read(address, bytes);
                value = Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(bytes));
                return read == size;
            }
            else
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
                bytes = buffer.AsSpan(0, size);

                int read = _reader.Read(address, bytes);
                value = Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(bytes));

                ArrayPool<byte>.Shared.Return(buffer);
                return read == size;
            }
        }

        public T Read<T>(ulong address) where T : unmanaged
        {
            Read(address, out T value);
            return value;
        }

        public bool ReadPointer(ulong address, out ulong value)
        {
            if (address == 0)
            {
                value = 0;
                return false;
            }

            if (PointerSize == 4)
            {
                bool res = Read(address, out uint ui);
                value = ui;
                return res;
            }

            return Read(address, out value);
        }

        public ulong ReadPointer(ulong address)
        {
            if (address == 0)
                return 0;

            if (PointerSize == 4)
                return Read<uint>(address);

            return Read<ulong>(address);
        }
    }

    public sealed class LruCache
    {
        private readonly object _sync = new();
        private readonly Dictionary<ulong, Entry> _lookup;
        private Entry? _head;
        private Entry? _tail;
        private int _capacity;
        private static int _multiRead;
        private static int _hits;
        private static int _misses;

        public static (int Hits, int Misses, int MultiRead) Stats => (_hits, _misses, _multiRead);

        public bool Multithreaded { get; }
        public int PageSize { get; }

        public LruCache(int capacity, int pageSize, bool multithreaded)
        {
            _lookup = new(capacity);
            _capacity = capacity;
            PageSize = pageSize;
            Multithreaded = multithreaded;
        }

        public Entry GetOrCreateCacheForAddress(ulong address)
        {
            ulong b = address & ~((ulong)PageSize - 1);
            Entry entry;
            bool locked = false;
            if (Multithreaded)
            {
                locked = true;
                Monitor.Enter(_sync);
            }
            try
            {
                if (_lookup.TryGetValue(b, out entry))
                {
                    _hits++;
                    return entry;
                }

                _misses++;
                if (_lookup.Count < _capacity)
                {
                    entry = new(this);
                    _tail ??= entry;
                }
                else
                {
                    entry = _tail ?? throw new NullReferenceException(nameof(_tail));

                    _lookup.Remove(entry.BaseAddress);
                    _tail = entry.Prev;
                    entry.ReleaseAndReset();
                }

                entry.BaseAddress = b;
                entry.Next = _head;
                if (_head is not null)
                    _head.Prev = entry;
                _head = entry;
                _lookup.Add(b, entry);
            }
            finally
            {
                if (locked)
                    Monitor.Exit(_sync);
            }

            return entry;
        }

        internal void ReportMultiRead()
        {
            Interlocked.Increment(ref _multiRead);
        }

        public class Entry
        {
            private LruCache _cache;
            private byte[]? _buffer;

            public ulong BaseAddress { get; set; }
            public Entry? Next { get; set; }
            public Entry? Prev { get; set; }
            public byte[]? Buffer => _buffer;

            public Entry(LruCache cache)
            {
                _cache = cache;
            }

            internal byte[] GetOrRead(IDataReader reader)
            {
                byte[]? buffer = _buffer;
                if (buffer is not null)
                    return buffer;

                buffer = ArrayPool<byte>.Shared.Rent(_cache.PageSize);
                int read = reader.Read(BaseAddress, buffer);
                if (read < buffer.Length)
                {
                    byte[] toReturn = buffer;
                    if (read == 0)
                        buffer = Array.Empty<byte>();
                    else
                        Array.Resize(ref buffer, read);

                    ArrayPool<byte>.Shared.Return(toReturn);
                }

                Interlocked.CompareExchange(ref _buffer, buffer, null);
                return _buffer ?? buffer;
            }

            public void ReleaseAndReset()
            {
                byte[]? buffer;
                if (_cache.Multithreaded)
                {
                    buffer = Interlocked.Exchange(ref _buffer, null);
                }
                else
                {
                    buffer = _buffer;
                    _buffer = null;
                }

                if (buffer is not null && buffer.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                Entry? prev = Prev;
                Entry? next = Next;
                if (Prev is not null)
                {
                    Debug.Assert(Prev.Next == null || Prev.Next == this);
                    Prev.Next = next;
                    Prev = null;
                }
                if (Next is not null)
                {
                    Debug.Assert(Next.Prev == null || Next.Prev == this);
                    Next.Prev = prev;
                    Next = null;
                }
            }

            public override string ToString() => BaseAddress.ToString("x");
        }
    }
}