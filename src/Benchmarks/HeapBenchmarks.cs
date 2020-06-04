using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.CsProj;
using BenchmarkDotNet.Toolchains.DotNetCli;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Perfolizer.Horology;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HeapBenchmarks
{
    public class Program
    {
        private DataTarget _dataTarget;
        private ClrRuntime _runtime;

        [Params(
            8 * 1024 * 1024,
            16 * 1024 * 1024,
            32 * 1024 * 1024,
            64 * 1024 * 1024,
            128 * 1024 * 1024,
            256 * 1024 * 1024,
            512 * 1024 * 1024,
            1024 * 1024 * 1024)]
        public int CacheSize { get; set; }

        [Params(true, false)]
        public bool UseAWE { get; set; }

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
                UseOSMemoryFeatures = UseAWE,
            };

            _dataTarget = DataTarget.LoadDump(@"C:\04_07_clrmd\talkexample5.dmp", options);   //@"D:\git\clrmd\src\TestTargets\bin\x64\GCRoot_wks.dmp", options);
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
            foreach (ClrObject obj in heap.EnumerateObjects())
            {
                foreach (ClrReference reference in obj.EnumerateReferencesWithFields(carefully:false, considerDependantHandles:true))
                {
                    _ = reference.Object;
                }
            }
        }


        [Benchmark]
        public void ParallelEnumerateHeapWithReferences()
        {
            ClrHeap heap = _runtime.Heap;
            Parallel.ForEach(heap.Segments, seg =>
            {
                foreach (ClrObject obj in seg.EnumerateObjects())
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
            Job job = Job.RyuJitX86.WithToolchain(CsProjCoreToolchain.From(settings))
                        .WithId("32bit")
                        .WithWarmupCount(1) // 1 warmup is enough for our purpose
                        .WithIterationTime(TimeInterval.FromSeconds(25)) // the default is 0.5s per iteration, which is slighlty too much for us
                        .WithMinIterationCount(10)
                        .WithMaxIterationCount(20) // we don't want to run more that 20 iterations
                        .DontEnforcePowerPlan(); // make sure BDN does not try to enforce High Performance power plan on Windows;

            config.AddJob(job);
            var summary = BenchmarkRunner.Run<Program>(config);

        }
    }
}
