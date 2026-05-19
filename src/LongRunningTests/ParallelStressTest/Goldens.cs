// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Runtime;

namespace ClrMD.Stress;

/// <summary>
/// A single-threaded reference snapshot for one CLR inside a dump. Workers
/// compare their parallel results against this snapshot to detect inconsistency.
/// </summary>
internal sealed class Golden
{
    /// <summary>Index into <see cref="DataTarget.ClrVersions"/>.</summary>
    public required int ClrIndex { get; init; }

    /// <summary>Every object on the heap, in heap-walk order. Used by HeapEnumerator workers.</summary>
    public required ClrObject[] Objects { get; init; }

    /// <summary>Number of GC roots reported by <see cref="ClrHeap.EnumerateRoots"/>.</summary>
    public required int RootCount { get; init; }

    /// <summary>The set of root object addresses (deduplicated, non-zero) used as BFS starting points.</summary>
    public required ulong[] RootObjectAddresses { get; init; }

    /// <summary>The number of distinct objects reachable from the roots via the per-thread BFS.</summary>
    public required int ReachableCount { get; init; }

    /// <summary>The ordered start addresses of each segment.</summary>
    public required ulong[] SegmentStarts { get; init; }
}

internal static class Goldens
{
    /// <summary>
    /// For each <see cref="ClrInfo"/> in the dump, try to compute a golden snapshot.
    /// CLRs whose runtime creation or single-threaded walks throw are silently
    /// excluded. The returned array is never null but may be empty.
    /// </summary>
    public static Golden[] Compute(DataTarget dt, Action<string> log)
    {
        List<Golden> result = new();
        for (int i = 0; i < dt.ClrVersions.Length; i++)
        {
            try
            {
                using ClrRuntime runtime = dt.ClrVersions[i].CreateRuntime();
                Golden g = ComputeOne(runtime, i, log);
                result.Add(g);
                log($"[golden]  clr[{i}] objects={g.Objects.Length:n0} roots={g.RootCount:n0} reachable={g.ReachableCount:n0} segments={g.SegmentStarts.Length}");
            }
            catch (Exception ex)
            {
                log($"[golden]  clr[{i}] DROPPED: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return result.ToArray();
    }

    private static Golden ComputeOne(ClrRuntime runtime, int clrIndex, Action<string> log)
    {
        ClrHeap heap = runtime.Heap;

        // Heap walk
        ClrObject[] objects = heap.EnumerateObjects().ToArray();

        // Segments
        ulong[] segmentStarts = heap.Segments.Select(s => s.ObjectRange.Start).ToArray();

        // Roots
        List<ClrRoot> roots = heap.EnumerateRoots().ToList();
        int rootCount = roots.Count;
        ulong[] rootAddresses = roots.Select(r => r.Object.Address)
                                     .Where(a => a != 0)
                                     .Distinct()
                                     .ToArray();

        // BFS reachability (mirrors the algorithm the workers will run)
        int reachable = BfsReachability.Walk(heap, rootAddresses);

        // Force the type cache to be populated before workers start hammering it.
        // We deliberately allocate a fresh array for the heap walk so workers can
        // index into the golden's snapshot by their own iteration count.
        return new Golden
        {
            ClrIndex = clrIndex,
            Objects = objects,
            RootCount = rootCount,
            RootObjectAddresses = rootAddresses,
            ReachableCount = reachable,
            SegmentStarts = segmentStarts,
        };
    }
}
