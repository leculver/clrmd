// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders;

Summary summary = BenchmarkRunner.Run<HeapWalkBenchmark>();
Console.WriteLine(summary);

public class HeapWalkBenchmark
{
    private DataTarget? _dataTarget;
    private ClrRuntime? _runtime;

    [Params(0x80, 0x200, 0x800, 0x1000, 0x2000)]
    public int PageSize { get; set; }

    [Params(0x10, 0x100, 0x1_000, 0x8_000, 0x10_000, 0x40_000)]
    public int PageCount { get; set; }

    [IterationSetup]
    public void IterationSetup()
    {
        CacheOptions options = new()
        {
            CachePageCount = PageCount,
            CachePageSize = PageSize,
            UseLru = true,
            UseOSMemoryFeatures = false,
        };

        _dataTarget = DataTarget.LoadDump(@"X:\Microsoft.Exchange.Diagnostics.Profiling.Agent003.DMP", options);
        // Set up your test environment using PageSize and PageCount

        if (_dataTarget is null)
            throw new InvalidOperationException();

        _runtime = _dataTarget.ClrVersions.Single().CreateRuntime();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _runtime?.Dispose();
        _dataTarget?.Dispose();
    }

    [Benchmark]
    public void TestHeapWalkWithReferences()
    {
        if (_runtime is null)
            throw new InvalidOperationException();

        foreach (ClrObject obj in _runtime.Heap.EnumerateObjects())
        {
            if (obj.ContainsPointers)
            {
                _ = obj.Type?.Name;
                foreach (ulong _ in obj.EnumerateReferenceAddresses())
                {

                }
            }
        }
    }
}