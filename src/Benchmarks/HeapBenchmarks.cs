﻿using BenchmarkDotNet.Attributes;
using Microsoft.Diagnostics.Runtime;
using System.Collections.Generic;
using System.Linq;

namespace Benchmarks
{
    // Single threaded heap enumeration test
    public class HeapBenchmarks
    {
        private DataTarget _dataTarget;
        private ClrRuntime _runtime;
        private ClrHeap _heap;

        [Params(
            32 * 1024 * 1024,
            128 * 1024 * 1024,
            256 * 1024 * 1024,
            512 * 1024 * 1024,
            1024 * 1024 * 1024)]
        public int CacheSize { get; set; }

        [ParamsSource(nameof(OSMemoryFeaturesSource))]
        public bool UseOSMemoryFeatures { get; set; }

        public IEnumerable<bool> OSMemoryFeaturesSource
        {
            get
            {
                yield return false;
                if (Program.ShouldTestOSMemoryFeatures)
                    yield return true;
            }
        }


        [GlobalSetup]
        public void Setup()
        {
            CacheOptions options = new CacheOptions()
            {
                CacheFields = true,
                CacheMethods = true,
                CacheTypes = true,

                CacheFieldNames = StringCaching.Cache,
                CacheMethodNames = StringCaching.Cache,
                CacheTypeNames = StringCaching.Cache,

                MaxDumpCacheSize = CacheSize,
                UseOSMemoryFeatures = UseOSMemoryFeatures,
            };

            _dataTarget = DataTarget.LoadDump(Program.CrashDump, options);
            _runtime = _dataTarget.ClrVersions.Single().CreateRuntime();
        }


        [IterationSetup]
        public void SetHeap()
        {
            _heap = _runtime.Heap;
        }

        [IterationCleanup]
        public void ClearCached()
        {
            _runtime.FlushCachedData();
        }

        [Benchmark]
        public void HeapEnumeration()
        {
            foreach (ClrObject _ in _heap.EnumerateObjects())
            {
            }
        }

        [Benchmark]
        public void HeapEnumerationWithReferences()
        {
            foreach (ClrObject obj in _heap.EnumerateObjects())
            {
                foreach (ClrObject _ in obj.EnumerateReferences(carefully:false, considerDependantHandles: false))
                {
                }
            }
        }
    }
}
