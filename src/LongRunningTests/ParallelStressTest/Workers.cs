// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Runtime;

namespace ClrMD.Stress;

internal enum WorkerKind
{
    HeapEnumerator,
    RootEnumerator,
    BfsReachability,
}

/// <summary>
/// Runs the BFS flood-fill the user specified:
///
///   queue todo;
///   push all root objects into todo;
///   set seen;
///   while todo {
///       pop todo -> curr;
///       if (!seen.Add(curr)) continue;
///       curr.EnumerateReferences() -> todo;
///   }
///
/// Each worker thread maintains its OWN <see cref="HashSet{T}"/> of seen
/// addresses and its OWN work queue, so we run N redundant graph walks in
/// parallel. They must all produce the same final reachable-count.
/// </summary>
internal static class BfsReachability
{
    public static int Walk(ClrHeap heap, ulong[] roots)
    {
        HashSet<ulong> seen = new();
        Stack<ulong> todo = new();
        foreach (ulong r in roots)
            todo.Push(r);

        while (todo.Count > 0)
        {
            ulong addr = todo.Pop();
            if (addr == 0)
                continue;

            if (!seen.Add(addr))
                continue;

            ClrObject obj = heap.GetObject(addr);
            if (!obj.IsValid || obj.IsNull)
                continue;

            foreach (ClrObject child in obj.EnumerateReferences(carefully: false, considerDependantHandles: true))
            {
                if (child.Address != 0 && !seen.Contains(child.Address))
                    todo.Push(child.Address);
            }
        }

        return seen.Count;
    }
}

/// <summary>
/// Spawns workers for every <see cref="ClrRuntime"/> in <paramref name="runtimes"/>,
/// each backed by its matching <see cref="Golden"/>. The work distribution is:
/// ~25% HeapEnumerator, ~25% RootEnumerator, ~50% BfsReachability per runtime.
///
/// All workers wait on a single start-gate so they hit the data reader at
/// the same instant. A background thread flushes the runtime's caches every
/// 500 ms while workers are running to maximize churn through the lock-free MMF
/// reader's per-thread segment cache.
/// </summary>
internal static class WorkerPool
{
    public static void RunOnce(ClrRuntime[] runtimes, Golden[] goldens, int totalThreads)
    {
        if (runtimes.Length != goldens.Length)
            throw new ArgumentException("runtimes and goldens must be the same length");
        if (runtimes.Length == 0)
            return;

        // Split threads across runtimes (at least 4 per runtime).
        int perRuntime = Math.Max(4, totalThreads / runtimes.Length);

        using ManualResetEventSlim gate = new(initialState: false);
        using CancellationTokenSource flusherStop = new();

        List<Thread> threads = new();
        List<Thread> flushers = new();

        for (int ri = 0; ri < runtimes.Length; ri++)
        {
            ClrRuntime runtime = runtimes[ri];
            Golden golden = goldens[ri];

            for (int ti = 0; ti < perRuntime; ti++)
            {
                WorkerKind kind = DistributeKind(ti, perRuntime);
                Thread t = new(() =>
                {
                    try
                    {
                        gate.Wait();
                        Run(kind, runtime, golden);
                    }
                    catch (Exception ex)
                    {
                        Failure.Fail(kind.ToString(), golden.ClrIndex, "unexpected worker exception", ex);
                    }
                })
                {
                    IsBackground = true,
                    Name = $"stress-clr{golden.ClrIndex}-{kind}-{ti}",
                };
                t.Start();
                threads.Add(t);
            }

            Thread flusher = new(() => FlusherLoop(runtime, flusherStop.Token))
            {
                IsBackground = true,
                Name = $"stress-clr{golden.ClrIndex}-flusher",
            };
            flusher.Start();
            flushers.Add(flusher);
        }

        gate.Set();

        foreach (Thread t in threads)
            t.Join();

        flusherStop.Cancel();
        foreach (Thread f in flushers)
            f.Join();
    }

    private static WorkerKind DistributeKind(int threadIndex, int perRuntime)
    {
        // First quarter heap, second quarter roots, rest BFS.
        int quarter = Math.Max(1, perRuntime / 4);
        if (threadIndex < quarter) return WorkerKind.HeapEnumerator;
        if (threadIndex < quarter * 2) return WorkerKind.RootEnumerator;
        return WorkerKind.BfsReachability;
    }

    private static void Run(WorkerKind kind, ClrRuntime runtime, Golden golden)
    {
        switch (kind)
        {
            case WorkerKind.HeapEnumerator:
                HeapWorker(runtime, golden);
                break;
            case WorkerKind.RootEnumerator:
                RootWorker(runtime, golden);
                break;
            case WorkerKind.BfsReachability:
                BfsWorker(runtime, golden);
                break;
            default:
                throw new InvalidOperationException($"Unknown WorkerKind {kind}");
        }
    }

    private static void HeapWorker(ClrRuntime runtime, Golden golden)
    {
        ClrHeap heap = runtime.Heap;

        // Segment-list sanity check first.
        var segments = heap.Segments;
        if (segments.Length != golden.SegmentStarts.Length)
        {
            Failure.Fail("HeapEnumerator", golden.ClrIndex,
                $"segment count mismatch: expected {golden.SegmentStarts.Length}, got {segments.Length}");
        }
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].ObjectRange.Start != golden.SegmentStarts[i])
            {
                Failure.Fail("HeapEnumerator", golden.ClrIndex,
                    $"segment[{i}] start mismatch: expected {golden.SegmentStarts[i]:x}, got {segments[i].ObjectRange.Start:x}");
            }
        }

        int count = 0;
        foreach (ClrObject obj in heap.EnumerateObjects())
        {
            if (count >= golden.Objects.Length)
            {
                Failure.Fail("HeapEnumerator", golden.ClrIndex,
                    $"heap walk produced more objects than golden ({golden.Objects.Length:n0}); next addr {obj.Address:x}");
                return;
            }

            ClrObject expected = golden.Objects[count];
            if (obj.Address != expected.Address)
            {
                Failure.Fail("HeapEnumerator", golden.ClrIndex,
                    $"object[{count}] address mismatch: expected {expected.Address:x}, got {obj.Address:x}");
                return;
            }

            // Type comparison: tolerate transient null on either side; both must agree.
            ClrType? gotType = obj.Type;
            ClrType? expType = expected.Type;
            if (gotType is null != expType is null)
            {
                Failure.Fail("HeapEnumerator", golden.ClrIndex,
                    $"object[{count}] {obj.Address:x} type-null mismatch: expected {expType?.Name ?? "<null>"}, got {gotType?.Name ?? "<null>"}");
                return;
            }
            if (gotType is not null && expType is not null && gotType.MethodTable != expType.MethodTable)
            {
                Failure.Fail("HeapEnumerator", golden.ClrIndex,
                    $"object[{count}] {obj.Address:x} type mismatch: expected {expType.Name} mt={expType.MethodTable:x}, got {gotType.Name} mt={gotType.MethodTable:x}");
                return;
            }

            count++;
        }

        if (count != golden.Objects.Length)
        {
            Failure.Fail("HeapEnumerator", golden.ClrIndex,
                $"object count mismatch: expected {golden.Objects.Length:n0}, got {count:n0}");
        }
    }

    private static void RootWorker(ClrRuntime runtime, Golden golden)
    {
        ClrHeap heap = runtime.Heap;
        int count = 0;
        foreach (ClrRoot _ in heap.EnumerateRoots())
            count++;

        if (count != golden.RootCount)
        {
            Failure.Fail("RootEnumerator", golden.ClrIndex,
                $"root count mismatch: expected {golden.RootCount:n0}, got {count:n0}");
        }
    }

    private static void BfsWorker(ClrRuntime runtime, Golden golden)
    {
        int reachable = BfsReachability.Walk(runtime.Heap, golden.RootObjectAddresses);
        if (reachable != golden.ReachableCount)
        {
            Failure.Fail("BfsReachability", golden.ClrIndex,
                $"reachable count mismatch: expected {golden.ReachableCount:n0}, got {reachable:n0}");
        }
    }

    private static void FlusherLoop(ClrRuntime runtime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ct.WaitHandle.WaitOne(500))
                    return;
                runtime.FlushCachedData();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                Failure.Fail("Flusher", null, "unexpected exception in cache flusher", ex);
            }
        }
    }
}
