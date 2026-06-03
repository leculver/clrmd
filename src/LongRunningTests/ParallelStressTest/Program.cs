// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace ClrMD.Stress;
internal enum StressReaderMode
{
    Standard,
    LockFree,
}


internal static class Program
{
    private const int DefaultDataTargetTeardownEvery = 5;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // --validate <dump> [--reader standard|lockfree]
        if (string.Equals(args[0], "--validate", StringComparison.Ordinal))
        {
            if (args.Length < 2)
            {
                PrintUsage();
                return 1;
            }

            StressReaderMode validationReaderMode = StressReaderMode.LockFree;
            for (int i = 2; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--reader", StringComparison.Ordinal))
                {
                    if (i + 1 >= args.Length || !TryParseReaderMode(args[++i], out validationReaderMode))
                    {
                        Console.Error.WriteLine("--reader requires 'standard' or 'lockfree'");
                        return 1;
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 1;
                }
            }

            return Validation.Run(args[1], validationReaderMode);
        }

        // --failfast-test (for smoke-testing dump capture)
        if (string.Equals(args[0], "--failfast-test", StringComparison.Ordinal))
        {
            Console.WriteLine("[failfast-test] calling Environment.FailFast in 100ms...");
            Thread.Sleep(100);
            Environment.FailFast("ClrMD stress --failfast-test verifying DOTNET_DbgEnableMiniDump captures");
            return 99;
        }

        string dumpPath = args[0];
        int timeoutSeconds = 7 * 60 + 30;   // default 7.5 min; outer script always passes --timeout
        int totalThreads = Environment.ProcessorCount;
        int dataTargetTeardownEvery = DefaultDataTargetTeardownEvery;
        StressReaderMode readerMode = StressReaderMode.LockFree;
        string? statsFile = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--timeout":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out timeoutSeconds))
                    {
                        Console.Error.WriteLine("--timeout requires an integer (seconds)");
                        return 1;
                    }
                    break;
                case "--threads":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out totalThreads))
                    {
                        Console.Error.WriteLine("--threads requires an integer");
                        return 1;
                    }
                    break;
                case "--dt-reload-every":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out dataTargetTeardownEvery))
                    {
                        Console.Error.WriteLine("--dt-reload-every requires an integer");
                        return 1;
                    }
                    break;
                case "--stats-file":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("--stats-file requires a path");
                        return 1;
                    }
                    statsFile = args[++i];
                    break;
                case "--reader":
                    if (i + 1 >= args.Length || !TryParseReaderMode(args[++i], out readerMode))
                    {
                        Console.Error.WriteLine("--reader requires 'standard' or 'lockfree'");
                        return 1;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return 1;
            }
        }

        if (!File.Exists(dumpPath))
        {
            Console.Error.WriteLine($"Dump not found: {dumpPath}");
            return 1;
        }

        Failure.DumpPath = dumpPath;
        Stats.DumpPath = dumpPath;
        Stats.StatsFile = statsFile;
        Stats.ThreadCount = totalThreads;
        Stats.ReaderMode = ToReaderModeName(readerMode);
        Stats.StartTime = Stopwatch.StartNew();

        Log($"[start]  dump={dumpPath} timeout={timeoutSeconds}s threads={totalThreads} dt-reload-every={dataTargetTeardownEvery} reader={ToReaderModeName(readerMode)} stats={statsFile ?? "<none>"}");

        // Hard watchdog: if the deadline + 90s elapses, force-exit. The main loop
        // only checks the deadline between iterations, so a single very slow iteration
        // (e.g. cold DAC walk on a >5GB dump under heavy lock contention) could hang
        // past the soft deadline indefinitely. The watchdog converts that into a
        // clean timeout exit so the outer wrapper can advance to the next dump.
        int hardTimeoutMs = (timeoutSeconds + 90) * 1000;
        Thread watchdog = new(() =>
        {
            try { Thread.Sleep(hardTimeoutMs); } catch { }
            try { Log($"[watchdog] hard deadline ({hardTimeoutMs / 1000}s) hit; forcing exit 0"); } catch { }
            Stats.WriteStatsLine("watchdog-timeout");
            Environment.Exit(0);
        }) { IsBackground = true, Name = "Watchdog" };
        watchdog.Start();

        DataTargetOptions options = CreateOptions(readerMode);

        DataTarget dt;
        Golden[] goldens;
        try
        {
            dt = DataTarget.LoadDump(dumpPath, options);
        }
        catch (Exception ex)
        {
            Log($"[fatal]  failed to load dump: {ex.GetType().Name}: {ex.Message}");
            Stats.WriteStatsLine("load-failed", $"{ex.GetType().Name}: {ex.Message}");
            return 4;
        }

        try
        {
            goldens = Goldens.Compute(dt, Log);
        }
        catch (Exception ex)
        {
            Log($"[fatal]  computing goldens threw {ex.GetType().Name}: {ex.Message}");
            Stats.WriteStatsLine("golden-failed", $"{ex.GetType().Name}: {ex.Message}");
            dt.Dispose();
            return 4;
        }

        if (goldens.Length == 0)
        {
            Log("[fatal]  no working ClrVersions; exiting 2 so outer script deletes the dump");
            Stats.WriteStatsLine("no-working-clr");
            dt.Dispose();
            return 2;
        }

        // Main parallel-stress loop.
        Stopwatch sw = Stopwatch.StartNew();
        int iteration = 0;
        long deadlineMs = (long)timeoutSeconds * 1000;
        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            iteration++;
            Stats.Iterations = iteration;
            Failure.CurrentIteration = iteration;

            // Recreate ClrRuntimes for the working CLR indices.
            ClrRuntime[] runtimes = new ClrRuntime[goldens.Length];
            try
            {
                for (int i = 0; i < goldens.Length; i++)
                    runtimes[i] = dt.ClrVersions[goldens[i].ClrIndex].CreateRuntime();
            }
            catch (Exception ex)
            {
                // Runtime creation should NOT throw if it worked during goldens computation.
                // Anything but an OOM here is suspicious and worth a FailFast.
                if (ex is OutOfMemoryException)
                {
                    Log($"[oom]    iteration {iteration}: {ex.Message}");
                    Cleanup(runtimes);
                    dt.Dispose();
                    Stats.WriteStatsLine("oom", ex.Message);
                    return 3;
                }
                Failure.Fail("CreateRuntime", null, "ClrRuntime creation failed after succeeding during golden computation", ex);
            }

            Stopwatch iter = Stopwatch.StartNew();
            try
            {
                WorkerPool.RunOnce(runtimes, goldens, totalThreads);
            }
            finally
            {
                Cleanup(runtimes);
            }

            // Periodic DataTarget reload to exercise full teardown of the MMF reader.
            if (dataTargetTeardownEvery > 0 && iteration % dataTargetTeardownEvery == 0)
            {
                dt.Dispose();
                try
                {
                    dt = DataTarget.LoadDump(dumpPath, options);
                }
                catch (Exception ex)
                {
                    Log($"[fatal]  DataTarget reload failed: {ex.GetType().Name}: {ex.Message}");
                    Stats.WriteStatsLine("reload-failed", $"{ex.GetType().Name}: {ex.Message}");
                    return 4;
                }
                Stats.DataTargetReloads++;
                Log($"[reload] iteration {iteration}: DataTarget reloaded");
            }

            // Also flush from the outside so back-to-back iterations don't share too much state.
            dt.DataReader.FlushCachedData();

            Log($"[iter]   {iteration} in {iter.Elapsed.TotalSeconds:F2}s (total {sw.Elapsed.TotalMinutes:F1}m / {timeoutSeconds / 60.0:F1}m) heapObj={Stats.HeapObjectsWalked:n0} bfsRefs={Stats.BfsRefsExpanded:n0}");
        }

        Log($"[done]   {iteration} iterations in {sw.Elapsed.TotalMinutes:F2} min; clean exit");
        Stats.WriteStatsLine("clean");
        dt.Dispose();
        return 0;
    }

    private static void Cleanup(ClrRuntime[] runtimes)
    {
        for (int i = 0; i < runtimes.Length; i++)
        {
            try { runtimes[i]?.Dispose(); }
            catch { /* dispose-time errors aren't a stress test failure on their own */ }
            runtimes[i] = null!;
        }
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {msg}");
        Console.Out.Flush();
    }

    internal static DataTargetOptions CreateOptions(StressReaderMode readerMode)
        => new()
        {
            UseLockFreeMemoryMapReader = readerMode == StressReaderMode.LockFree,
            VerifyDacOnWindows = false,
        };

    internal static string ToReaderModeName(StressReaderMode readerMode)
        => readerMode == StressReaderMode.LockFree ? "lockfree" : "standard";

    private static bool TryParseReaderMode(string value, out StressReaderMode readerMode)
    {
        if (string.Equals(value, "lockfree", StringComparison.OrdinalIgnoreCase))
        {
            readerMode = StressReaderMode.LockFree;
            return true;
        }

        if (string.Equals(value, "standard", StringComparison.OrdinalIgnoreCase))
        {
            readerMode = StressReaderMode.Standard;
            return true;
        }

        readerMode = default;
        return false;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  stress.exe <dump-path> [--timeout SECS] [--threads N] [--dt-reload-every N] [--stats-file PATH] [--reader standard|lockfree]");
        Console.Error.WriteLine("  stress.exe --validate <dump-path> [--reader standard|lockfree]");
    }
}
