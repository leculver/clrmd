using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows.Configs;
using BenchmarkDotNet.Horology;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using Microsoft.Diagnostics.Runtime;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HeapBenchmarks
{
    public enum MemoryReader
    {
        AWE,
        ArrayPool,
        Cached
    }

    public class Program
    {
        private DataTarget _dataTarget;
        private ClrRuntime _runtime;

        [Params(
            64 * 1024 * 1024,
            256 * 1024 * 1024,
            512 * 1024 * 1024,
            1024 * 1024 * 1024)]
        public int CacheSize { get; set; }


        [Params(1024, 2048, 4096, 8192, 32768)]
        public int PageSize { get; set; }

        [Params(MemoryReader.Cached)]  //MemoryReader.AWE, MemoryReader.ArrayPool, 
        public MemoryReader Reader { get; set; }

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

                PageSize = PageSize,
                MaxDumpCacheSize = CacheSize,
            };

            switch (Reader)
            {
                case MemoryReader.Cached:
                    options.UseNewMemoryReader = true;
                    break;

                case MemoryReader.AWE:
                    options.UseNewMemoryReader = true;
                    options.UseOSMemoryFeatures = true;
                    break;

                case MemoryReader.ArrayPool:
                    options.UseNewMemoryReader = true;
                    options.UseOSMemoryFeatures = false;
                    break;
            }


            _dataTarget = DataTarget.LoadDump(@"c:\git\talkexample5.dmp", options);   //@"D:\git\clrmd\src\TestTargets\bin\x64\GCRoot_wks.dmp", options);
            _runtime = _dataTarget.ClrVersions.Single().CreateRuntime();
        }

        [IterationCleanup]
        public void ClearCached()
        {
            _runtime.FlushCachedData();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _dataTarget?.Dispose();
        }

        [Benchmark]
        public void EnumerateHeapWithReferences()
        {
            ClrHeap heap = _runtime.Heap;
            foreach (ClrSegment seg in heap.Segments)
            {
                foreach (ClrObject obj in seg.EnumerateObjects().Take(2048))
                {
                    foreach (ClrReference reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
                    {
                        _ = reference.Object;
                    }
                }
            }
        }


        [Benchmark]
        public void ParallelEnumerateHeapWithReferences()
        {
            ClrHeap heap = _runtime.Heap;
            Parallel.ForEach(heap.Segments, seg =>
            {
                foreach (ClrObject obj in seg.EnumerateObjects().Take(2048))
                {
                    foreach (ClrReference reference in obj.EnumerateReferencesWithFields(carefully: false, considerDependantHandles: true))
                    {
                        _ = reference.Object;
                    }
                }
            });

        }

        static void Main(string[] _)
        {
            var settings = NetCoreAppSettings.NetCoreApp31.WithCustomDotNetCliPath(@"C:\Program Files (x86)\dotnet\dotnet.exe");
            var config = ManualConfig.Create(DefaultConfig.Instance);
            Job job = Job.RyuJitX86.With(CsProjCoreToolchain.From(settings))
                        .WithId("32bit")
                        .WithWarmupCount(1) // 1 warmup is enough for our purpose
                        .WithIterationTime(TimeInterval.FromSeconds(25)) // the default is 0.5s per iteration, which is slighlty too much for us
                        .WithMinIterationCount(4)
                        .WithMaxIterationCount(5) // we don't want to run more that 20 iterations
                        .DontEnforcePowerPlan(); // make sure BDN does not try to enforce High Performance power plan on Windows;

            config.Add(job);
            var summary = BenchmarkRunner.Run<Program>(config);

        }
    }
}
