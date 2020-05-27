﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Diagnostics.Runtime.Windows
{
    internal sealed class MinidumpMemoryReader : IMemoryReader
    {
        private readonly MinidumpSegment[] _segments;
        private readonly Stream _stream;
        private readonly object _sync = new object();

        private readonly NativeMemory _nativeMemory;

        public MinidumpMemoryReader(MinidumpSegment[] segments, string dumpPath, Stream stream, int pointerSize)
        {
            _segments = segments;
            _stream = stream;
            PointerSize = pointerSize;

            _nativeMemory = new NativeMemory(pointerSize, dumpPath, 0x800_0000, CacheTechnology.ArrayPool, _segments.ToImmutableArray());
        }

        public int PointerSize { get; }

        public int ReadFromRVA(long rva, Span<byte> buffer)
        {
            // todo: test bounds
            lock (_sync)
            {
                if (_stream.Length <= rva)
                    return 0;

                _stream.Position = rva;
                return _stream.Read(buffer);
            }
        }

        public string? ReadCountedUnicode(long rva)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
            try
            {
                int count;

                lock (_sync)
                {
                    _stream.Position = rva;

                    Span<byte> span = new Span<byte>(buffer);
                    if (_stream.Read(span.Slice(0, sizeof(int))) != sizeof(int))
                        return null;

                    int len = Unsafe.As<byte, int>(ref buffer[0]);
                    len = Math.Min(len, buffer.Length);

                    if (len <= 0)
                        return null;

                    count = _stream.Read(buffer, 0, len);
                }

                return Encoding.Unicode.GetString(buffer, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public unsafe bool Read<T>(ulong address, out T value) where T : unmanaged
        {
            Span<byte> buffer = stackalloc byte[sizeof(T)];
            if (Read(address, buffer) == buffer.Length)
            {
                value = Unsafe.As<byte, T>(ref MemoryMarshal.GetReference(buffer));
                return true;
            }

            value = default;
            return false;
        }

        public T Read<T>(ulong address) where T : unmanaged
        {
            Read(address, out T t);
            return t;
        }

        public bool ReadPointer(ulong address, out ulong value)
        {
            Span<byte> buffer = stackalloc byte[PointerSize];
            if (Read(address, buffer) == PointerSize)
            {
                value = buffer.AsPointer();
                return true;
            }

            value = 0;
            return false;
        }

        public ulong ReadPointer(ulong address)
        {
            if (ReadPointer(address, out ulong value))
                return value;

            return 0;
        }

        public int Read(ulong address, Span<byte> buffer)
        {
            if (address == 0)
                return 0;

            if (_nativeMemory.TryReadMemory(address, (uint)buffer.Length, buffer))
            {
                return buffer.Length;
            }


            lock (_sync)
            {
                try
                {
                    int bytesRead = 0;

                    while (bytesRead < buffer.Length)
                    {
                        ulong currAddress = address + (uint)bytesRead;
                        int curr = GetSegmentContaining(currAddress);
                        if (curr == -1)
                            break;

                        ref MinidumpSegment seg = ref _segments[curr];
                        ulong offset = currAddress - seg.VirtualAddress;

                        Span<byte> slice = buffer.Slice(bytesRead, Math.Min(buffer.Length - bytesRead, (int)(seg.Size - offset)));
                        _stream.Position = (long)(seg.FileOffset + offset);
                        int read = _stream.Read(slice);
                        if (read == 0)
                            break;

                        bytesRead += read;
                    }

                    return bytesRead;
                }
                catch (IOException)
                {
                    return 0;
                }
            }
        }

        private int IndexOf(ulong end, MinidumpSegment[] segments)
        {
            for (int i = 0; i < segments.Length; i++)
                if (segments[i].Contains(end))
                    return i;

            return -1;
        }

        private int GetSegmentContaining(ulong address)
        {
            int result = -1;
            int lower = 0;
            int upper = _segments.Length - 1;

            while (lower <= upper)
            {
                int mid = (lower + upper) >> 1;
                ref MinidumpSegment seg = ref _segments[mid];

                if (seg.Contains(address))
                {
                    result = mid;
                    break;
                }

                if (address < seg.VirtualAddress)
                    upper = mid - 1;
                else
                    lower = mid + 1;
            }

            return result;
        }
    }
}
