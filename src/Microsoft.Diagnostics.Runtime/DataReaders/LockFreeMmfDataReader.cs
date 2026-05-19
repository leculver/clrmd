// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;

namespace Microsoft.Diagnostics.Runtime.Implementation
{
    /// <summary>
    /// A lock-free <see cref="IDataReader"/> that satisfies memory reads directly from a
    /// memory-mapped view of the dump file. Eliminates per-read locks and stream seeks
    /// compared to the default cached/uncached minidump readers.
    ///
    /// <para>
    /// Construction strategy: the inner <see cref="IDataReader"/> (a normal
    /// <c>MinidumpReader</c>, <c>CoredumpReader</c>, or <c>MachOCoreReader</c>) is created
    /// first so the existing parsers do all format-specific work. The lock-free reader then
    /// pulls the resulting (VA, FileOffset, Size) segment table via
    /// <see cref="IDumpFileMemorySource"/> — no parsing logic is duplicated.
    /// </para>
    ///
    /// <para>
    /// Hot path: a sorted segment array + a per-thread (striped) last-used-segment cache
    /// provide O(1) access for the dominant sequential-read pattern (heap walks, GC root
    /// traversal), falling back to binary search for non-local addresses. The stripe is
    /// indexed by <see cref="Environment.CurrentManagedThreadId"/>, padded to a cache line
    /// to avoid false sharing. Multiple threads can read concurrently with zero locking.
    /// </para>
    ///
    /// <para>
    /// All non-memory operations (modules, thread contexts, OS thread enumeration) delegate
    /// to the inner reader. If the inner reader does not report
    /// <see cref="IDataReader.IsThreadSafe"/> as <c>true</c>, those delegated calls are
    /// serialized through an instance-level lock. Memory reads never take this lock.
    /// </para>
    ///
    /// <para>
    /// Thread safety: concurrent calls to <see cref="Read(ulong, Span{byte})"/>,
    /// <see cref="TryGetDirectSpan(ulong, int, out ReadOnlySpan{byte})"/>, and the typed
    /// <c>Read</c>/<c>ReadPointer</c> overloads are safe. <see cref="Dispose"/> must not
    /// race with active reads — the standard .NET disposal contract applies.
    /// </para>
    /// </summary>
    internal sealed class LockFreeMmfDataReader : IDataReader, IThreadReader, IDumpInfoProvider, ISegmentedDirectMemoryAccess, IDisposable, IThreadInfoReader, IProcessInfoProvider, IMemoryRegionReader, IModuleSegmentReader
    {
        private readonly IDataReader _inner;
        private readonly IThreadReader? _innerThreads;
        private readonly IDumpInfoProvider? _innerInfo;
        private readonly IThreadInfoReader? _innerThreadInfo;
        private readonly IProcessInfoProvider? _innerProcessInfo;
        private readonly IMemoryRegionReader? _innerRegions;
        private readonly IModuleSegmentReader? _innerModuleSegments;
        // Non-null only when the inner reader is not itself thread-safe. Memory reads never
        // touch this lock; only delegated cold-path calls (modules, threads, contexts) do.
        private readonly object? _innerLock;
        private readonly MemoryMappedFile _mmf;
        private readonly MemoryMappedViewAccessor _accessor;
        // Base address of the mapped view, stored as IntPtr so the field itself is not unsafe.
        // Acquired via SafeMemoryMappedViewHandle.AcquirePointer in the ctor and released in
        // Dispose. All raw-pointer use is confined to the ViewSpan helper below; the helper
        // exists because no safe API can materialize a Span<byte> over an MMF view, and zero-copy
        // spans over the dump file are the entire reason this reader exists.
        private readonly IntPtr _basePtr;
        private readonly long _viewLength;
        private readonly Segment[] _segments;
        // Per-thread last-used-segment cache, striped by managed thread id. Each slot is
        // padded to 64 bytes to prevent false sharing between adjacent stripes. A torn read
        // is impossible (int is atomic on .NET) and a stale value just causes a cache miss
        // that falls back to binary search via FindSegment.
        private readonly PaddedInt[] _lastSegmentByStripe;
        private readonly int _stripeMask;
        private bool _disposed;

        // Cache-line sized container for an int. The 64-byte size is a generous floor that
        // covers x64 (64), Apple silicon (128 — we accept some sharing there), and most ARM.
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        private struct PaddedInt
        {
            [FieldOffset(0)]
            public int Value;
        }

        private readonly struct Segment
        {
            public readonly ulong VirtualAddress;
            public readonly ulong Size;
            public readonly ulong FileOffset;

            public Segment(ulong virtualAddress, ulong size, ulong fileOffset)
            {
                VirtualAddress = virtualAddress;
                Size = size;
                FileOffset = fileOffset;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool Contains(ulong address) => address >= VirtualAddress && address - VirtualAddress < Size;
        }

        /// <summary>
        /// Wraps <paramref name="inner"/> with a memory-mapped, lock-free read path over
        /// the same dump file.
        /// </summary>
        /// <param name="dumpPath">Path to the dump file on disk.</param>
        /// <param name="inner">An inner reader that also implements <see cref="IDumpFileMemorySource"/>.</param>
        public LockFreeMmfDataReader(string dumpPath, IDataReader inner)
        {
            if (dumpPath is null)
                throw new ArgumentNullException(nameof(dumpPath));
            if (inner is null)
                throw new ArgumentNullException(nameof(inner));

            if (inner is not IDumpFileMemorySource segmentSource)
                throw new ArgumentException(
                    $"Inner reader of type {inner.GetType().Name} does not implement {nameof(IDumpFileMemorySource)}.",
                    nameof(inner));

            _inner = inner;
            _innerThreads = inner as IThreadReader;
            _innerInfo = inner as IDumpInfoProvider;
            _innerThreadInfo = inner as IThreadInfoReader;
            _innerProcessInfo = inner as IProcessInfoProvider;
            _innerRegions = inner as IMemoryRegionReader;
            _innerModuleSegments = inner as IModuleSegmentReader;
            // Only synchronize cold-path delegation when the inner cannot do it itself.
            _innerLock = inner.IsThreadSafe ? null : new object();

            MemoryMappedFile? mmf = null;
            MemoryMappedViewAccessor? accessor = null;
            FileStream? fs = null;
            try
            {
                // Open the file ourselves with FileShare.Read|Write|Delete so we don't conflict with
                // the inner reader (which already has the file open with FileShare.Read).
                // MemoryMappedFile.CreateFromFile(string, ...) opens with FileShare.None internally,
                // which would throw "file in use" against the inner reader's existing handle.
                fs = new FileStream(dumpPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                mmf = MemoryMappedFile.CreateFromFile(fs, mapName: null, capacity: 0, MemoryMappedFileAccess.Read,
                    HandleInheritability.None, leaveOpen: false);
                accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                _viewLength = (long)accessor.SafeMemoryMappedViewHandle.ByteLength;

                unsafe
                {
                    byte* ptr = null;
                    accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                    _basePtr = (IntPtr)(ptr + accessor.PointerOffset);
                }

                _mmf = mmf;
                _accessor = accessor;
            }
            catch (Exception ex) when (IsAddressSpaceExhaustion(ex))
            {
                accessor?.Dispose();
                mmf?.Dispose();
                fs?.Dispose();
                throw new OutOfMemoryException(BuildOomMessage(dumpPath), ex);
            }

            _segments = BuildSegments(segmentSource.EnumerateMemorySegments());

            // Size the stripe array to ProcessorCount * 2 (rounded up to a power of two,
            // minimum 4). This covers oversubscription cheaply — total footprint is
            // 64 * stripes bytes (~1–2 KB on typical machines).
            int stripes = RoundUpToPowerOfTwo(Math.Max(4, Environment.ProcessorCount * 2));
            _lastSegmentByStripe = new PaddedInt[stripes];
            _stripeMask = stripes - 1;
        }

        private static int RoundUpToPowerOfTwo(int value)
        {
            // value is always >= 4 here; this is the standard bithack rounded up.
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int CurrentStripe() => Environment.CurrentManagedThreadId & _stripeMask;

        private static bool IsAddressSpaceExhaustion(Exception ex)
            => ex is OutOfMemoryException or IOException;

        private static string BuildOomMessage(string dumpPath)
        {
            long fileSize = -1;
            try { fileSize = new FileInfo(dumpPath).Length; } catch { }

            string sizeNote = fileSize >= 0
                ? $" (dump file is {fileSize / (1024.0 * 1024.0 * 1024.0):F2} GB)"
                : string.Empty;

            if (!Environment.Is64BitProcess)
            {
                return $"Failed to memory-map dump file{sizeNote}. " +
                    $"This is most likely caused by 32-bit address space exhaustion: " +
                    $"{nameof(DataTargetOptions)}.{nameof(DataTargetOptions.UseLockFreeMemoryMapReader)} " +
                    $"maps the entire dump file into the current process. " +
                    $"Disable {nameof(DataTargetOptions.UseLockFreeMemoryMapReader)} (the default) " +
                    $"or run as a 64-bit process to load large dumps.";
            }

            return $"Failed to memory-map dump file{sizeNote} while initializing the lock-free " +
                $"memory-mapped data reader ({nameof(DataTargetOptions.UseLockFreeMemoryMapReader)}). " +
                $"Disable {nameof(DataTargetOptions.UseLockFreeMemoryMapReader)} (the default) to fall back " +
                $"to the streaming reader.";
        }

        private Segment[] BuildSegments(IReadOnlyList<DumpMemorySegment> source)
        {
            int count = source.Count;
            Segment[] result = new Segment[count];
            for (int i = 0; i < count; i++)
            {
                DumpMemorySegment s = source[i];
                if (s.FileOffset > (ulong)_viewLength || s.FileOffset + s.Size > (ulong)_viewLength)
                {
                    // Truncate / skip out-of-range entries rather than fail outright; the
                    // inner reader can fall back if needed for cold paths (we never invoke it
                    // for memory reads, so an empty slot just produces a 0-byte read).
                    result[i] = new Segment(s.VirtualAddress, 0, s.FileOffset);
                    continue;
                }
                result[i] = new Segment(s.VirtualAddress, s.Size, s.FileOffset);
            }

            // The contract of IDumpFileMemorySource requires sorted-by-VA, but be defensive.
            Array.Sort(result, static (a, b) => a.VirtualAddress.CompareTo(b.VirtualAddress));
            return result;
        }

        // ============================================================
        // IMemoryReader — hot path (lock-free pointer reads)
        // ============================================================

        public int PointerSize => _inner.PointerSize;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Read(ulong address, Span<byte> buffer)
        {
            if (buffer.Length == 0)
                return 0;

            int stripe = CurrentStripe();
            int idx;
            int lastIdx = _lastSegmentByStripe[stripe].Value;
            if ((uint)lastIdx < (uint)_segments.Length && _segments[lastIdx].Contains(address))
            {
                idx = lastIdx;
            }
            else
            {
                idx = FindSegment(address);
                if (idx < 0)
                    return 0;
                _lastSegmentByStripe[stripe].Value = idx;
            }

            int bytesRead = ReadFromSegment(ref _segments[idx], address, buffer);

            // Span adjacent segments transparently — matches the existing minidump readers.
            while (bytesRead < buffer.Length)
            {
                int nextIdx = idx + 1;
                if ((uint)nextIdx >= (uint)_segments.Length)
                    break;

                ulong nextAddress = address + (ulong)bytesRead;
                ref Segment nextSeg = ref _segments[nextIdx];
                if (!nextSeg.Contains(nextAddress))
                    break;

                int additional = ReadFromSegment(ref nextSeg, nextAddress, buffer.Slice(bytesRead));
                if (additional == 0)
                    break;

                bytesRead += additional;
                idx = nextIdx;
                _lastSegmentByStripe[stripe].Value = idx;
            }

            return bytesRead;
        }

        public bool Read<T>(ulong address, out T value) where T : unmanaged
        {
            Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
            if (Read(address, buffer) != buffer.Length)
            {
                value = default;
                return false;
            }
            value = MemoryMarshal.Read<T>(buffer);
            return true;
        }

        public T Read<T>(ulong address) where T : unmanaged
        {
            Read(address, out T value);
            return value;
        }

        public bool ReadPointer(ulong address, out ulong value)
        {
            int ps = PointerSize;
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            // stackalloc zero-inits, so the upper 4 bytes remain 0 on 32-bit reads.
            if (Read(address, buffer.Slice(0, ps)) != ps)
            {
                value = 0;
                return false;
            }
            value = ps == sizeof(ulong) ? MemoryMarshal.Read<ulong>(buffer) : MemoryMarshal.Read<uint>(buffer);
            return true;
        }

        public ulong ReadPointer(ulong address)
        {
            ReadPointer(address, out ulong value);
            return value;
        }

        // Single chokepoint for materializing a Span<byte> over the mapped view.
        // This is the only method that dereferences _basePtr.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe ReadOnlySpan<byte> ViewSpan(long fileOffset, int length)
            => new((byte*)_basePtr + fileOffset, length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadFromSegment(ref Segment seg, ulong address, Span<byte> buffer)
        {
            ulong offsetInSeg = address - seg.VirtualAddress;
            ulong availableU = seg.Size - offsetInSeg;
            if (availableU == 0)
                return 0;

            int toRead = buffer.Length;
            if (availableU < (ulong)toRead)
                toRead = (int)availableU;

            ulong fileOffset = seg.FileOffset + offsetInSeg;
            ViewSpan((long)fileOffset, toRead).CopyTo(buffer);
            return toRead;
        }

        // ============================================================
        // ISegmentedDirectMemoryAccess — zero-copy hot path
        // ============================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetDirectSpan(ulong address, int length, out ReadOnlySpan<byte> span)
        {
            if (length == 0)
            {
                span = default;
                return true;
            }

            if (length < 0)
            {
                span = default;
                return false;
            }

            int stripe = CurrentStripe();
            int idx;
            int lastIdx = _lastSegmentByStripe[stripe].Value;
            if ((uint)lastIdx < (uint)_segments.Length && _segments[lastIdx].Contains(address))
            {
                idx = lastIdx;
            }
            else
            {
                idx = FindSegment(address);
                if (idx < 0)
                {
                    span = default;
                    return false;
                }
                _lastSegmentByStripe[stripe].Value = idx;
            }

            ref Segment seg = ref _segments[idx];
            ulong offsetInSeg = address - seg.VirtualAddress;
            ulong availableU = seg.Size - offsetInSeg;
            if (availableU < (ulong)length)
            {
                // Range crosses a segment boundary (or extends past the segment end);
                // caller falls back to Read which transparently spans adjacent segments.
                span = default;
                return false;
            }

            ulong fileOffset = seg.FileOffset + offsetInSeg;
            if (fileOffset > (ulong)_viewLength || fileOffset + (ulong)length > (ulong)_viewLength)
            {
                span = default;
                return false;
            }

            span = ViewSpan((long)fileOffset, length);
            return true;
        }

        private int FindSegment(ulong address)
        {
            int lo = 0;
            int hi = _segments.Length - 1;

            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                ref Segment seg = ref _segments[mid];

                if (address < seg.VirtualAddress)
                    hi = mid - 1;
                else if (!seg.Contains(address))
                    lo = mid + 1;
                else
                    return mid;
            }

            return -1;
        }

        // ============================================================
        // IDataReader — cold path (delegated to inner)
        // ============================================================

        public string DisplayName => _inner.DisplayName;
        public bool IsThreadSafe => true;
        public OSPlatform TargetPlatform => _inner.TargetPlatform;
        public Architecture Architecture => _inner.Architecture;
        public int ProcessId => _inner.ProcessId;

        public IEnumerable<ModuleInfo> EnumerateModules()
        {
            if (_innerLock is null)
                return _inner.EnumerateModules();

            // Materialize under the lock; lazy enumeration would otherwise leak across threads.
            lock (_innerLock)
                return new List<ModuleInfo>(_inner.EnumerateModules());
        }

        public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context)
        {
            if (_innerLock is null)
                return _inner.GetThreadContext(threadID, contextFlags, context);

            lock (_innerLock)
                return _inner.GetThreadContext(threadID, contextFlags, context);
        }

        public void FlushCachedData()
        {
            // Reset every stripe so stale cache entries don't survive across a flush.
            for (int i = 0; i < _lastSegmentByStripe.Length; i++)
                _lastSegmentByStripe[i].Value = 0;

            if (_innerLock is null)
                _inner.FlushCachedData();
            else
                lock (_innerLock)
                    _inner.FlushCachedData();
        }

        // IThreadReader (passthrough so DAC OS thread enumeration keeps working)
        public IEnumerable<uint> EnumerateOSThreadIds()
        {
            if (_innerThreads is null)
                return Array.Empty<uint>();

            if (_innerLock is null)
                return _innerThreads.EnumerateOSThreadIds();

            lock (_innerLock)
                return new List<uint>(_innerThreads.EnumerateOSThreadIds());
        }

        public ulong GetThreadTeb(uint osThreadId)
        {
            if (_innerThreads is null)
                return 0;

            if (_innerLock is null)
                return _innerThreads.GetThreadTeb(osThreadId);

            lock (_innerLock)
                return _innerThreads.GetThreadTeb(osThreadId);
        }

        // IDumpInfoProvider passthrough. These properties are computed once by the inner
        // and read-only thereafter, so no locking is required.
        public bool IsMiniOrTriage => _innerInfo?.IsMiniOrTriage ?? false;
        public bool IsCreatedByDotNetRuntime => _innerInfo?.IsCreatedByDotNetRuntime ?? false;

        // IThreadInfoReader passthrough
        public bool TryGetThreadInfo(uint osThreadId, out ThreadInfo info)
        {
            if (_innerThreadInfo is null)
                throw CreateUnsupportedInterfaceException(nameof(IThreadInfoReader));

            if (_innerLock is null)
                return _innerThreadInfo.TryGetThreadInfo(osThreadId, out info);

            lock (_innerLock)
                return _innerThreadInfo.TryGetThreadInfo(osThreadId, out info);
        }

        public IEnumerable<ThreadInfo> EnumerateThreadInfo()
        {
            if (_innerThreadInfo is null)
                throw CreateUnsupportedInterfaceException(nameof(IThreadInfoReader));

            if (_innerLock is null)
                return _innerThreadInfo.EnumerateThreadInfo();

            lock (_innerLock)
                return new List<ThreadInfo>(_innerThreadInfo.EnumerateThreadInfo());
        }

        // IProcessInfoProvider passthrough
        public ProcessInfo GetProcessInfo()
        {
            if (_innerProcessInfo is null)
                throw CreateUnsupportedInterfaceException(nameof(IProcessInfoProvider));

            if (_innerLock is null)
                return _innerProcessInfo.GetProcessInfo();

            lock (_innerLock)
                return _innerProcessInfo.GetProcessInfo();
        }

        // IMemoryRegionReader passthrough
        public IEnumerable<MemoryRegion> EnumerateMemoryRegions()
        {
            if (_innerRegions is null)
                throw CreateUnsupportedInterfaceException(nameof(IMemoryRegionReader));

            if (_innerLock is null)
                return _innerRegions.EnumerateMemoryRegions();

            lock (_innerLock)
                return new List<MemoryRegion>(_innerRegions.EnumerateMemoryRegions());
        }

        // IModuleSegmentReader passthrough
        public IEnumerable<ModuleSegment> EnumerateModuleSegments(ulong moduleBaseAddress)
        {
            if (_innerModuleSegments is null)
                throw CreateUnsupportedInterfaceException(nameof(IModuleSegmentReader));

            if (_innerLock is null)
                return _innerModuleSegments.EnumerateModuleSegments(moduleBaseAddress);

            lock (_innerLock)
                return new List<ModuleSegment>(_innerModuleSegments.EnumerateModuleSegments(moduleBaseAddress));
        }

        private static NotSupportedException CreateUnsupportedInterfaceException(string interfaceName)
            => new($"The inner data reader does not implement {interfaceName}.");

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_inner is IDisposable d)
                d.Dispose();
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _accessor.Dispose();
            _mmf.Dispose();
        }
    }
}
