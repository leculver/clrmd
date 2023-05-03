// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders;

bool useArrayPool;
bool useLru;
switch (args[1].ToLower())
{
    case "lru":
        useLru = true;
        useArrayPool = false;
        break;

    case "arraypool":
        useLru = false;
        useArrayPool = true;
        break;

    case "none":
        useLru = false;
        useArrayPool = false;
        break;

    default:
        throw new ArgumentException(nameof(args));
}


CacheOptions cacheOptions = new() { UseLru = useLru, UseArrayPool = useArrayPool };
using DataTarget dataTarget = DataTarget.LoadDump(args[0], cacheOptions);
using ClrRuntime runtime = dataTarget.ClrVersions.Single().CreateRuntime();

HashSet<ulong> seen = new();

foreach (ClrRoot root in runtime.Heap.EnumerateRoots())
{
    seen.Add(root.Object);
}

Console.WriteLine($"Found {seen.Count:n0} root objects.");

HashSet<string> names = new();


int count = 0;

Stopwatch sw = Stopwatch.StartNew();
foreach (ClrObject obj in runtime.Heap.EnumerateObjects())
{
    if ((++count % 100_000) == 0)
        Console.Title = $"{count:n0} objects";

    if (names.Contains(obj.Type?.Name ?? ""))
        continue;

    foreach (ClrObject child in obj.EnumerateReferences())
    {
        if (seen.Contains(child))
            break;
    }
}

Console.WriteLine(count.ToString("n0"));

sw.Stop();
GC.Collect();

Console.WriteLine(sw.Elapsed);
Console.WriteLine($"Considered {seen.Count:n0} objects");
Console.WriteLine($"WorkingSet: {Process.GetCurrentProcess().WorkingSet64:n0}");
Console.WriteLine($"PeakWorkingSet: {Process.GetCurrentProcess().PeakWorkingSet64:n0}");
(int Hits, int Misses, int MultiRead, int UnalignedRead) stats = LruCache.Stats;
Console.WriteLine($"hits:{stats.Hits:n0} misses:{stats.Misses:n0} multi:{stats.MultiRead:n0} unaligned:{stats.UnalignedRead:n0}");