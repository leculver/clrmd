// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace ClrMD.Stress;

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

        // --validate <dump>
        if (string.Equals(args[0], "--validate", StringComparison.Ordinal))
        {
            if (args.Length != 2)
            {
                PrintUsage();
                return 1;
            }
            return Validation.Run(args[1]);
        }

        string dumpPath = args[0];
        int timeoutSeconds = 7 * 60 + 30;   // default 7.5 min; outer script always passes --timeout
        int totalThreads = Environment.ProcessorCount;
        int dataTargetTeardownEvery = DefaultDataTargetTeardownEvery;

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
        Log($"[start]  dump={dumpPath} timeout={timeoutSeconds}s threads={totalThreads} dt-reload-every={dataTargetTeardownEvery}");

        DataTargetOptions options = new() { UseLockFreeMemoryMapReader = true };

        DataTarget dt;
        Golden[] goldens;
        try
        {
            dt = DataTarget.LoadDump(dumpPath, options);
        }
        catch (Exception ex)
        {
            Log($"[fatal]  failed to load dump: {ex.GetType().Name}: {ex.Message}");
            return 4;
        }

        try
        {
            goldens = Goldens.Compute(dt, Log);
        }
        catch (Exception ex)
        {
            Log($"[fatal]  computing goldens threw {ex.GetType().Name}: {ex.Message}");
            dt.Dispose();
            return 4;
        }

        if (goldens.Length == 0)
        {
            Log("[fatal]  no working ClrVersions; exiting 2 so outer script deletes the dump");
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
                    return 4;
                }
                Log($"[reload] iteration {iteration}: DataTarget reloaded");
            }

            // Also flush from the outside so back-to-back iterations don't share too much state.
            dt.DataReader.FlushCachedData();

            Log($"[iter]   {iteration} in {iter.Elapsed.TotalSeconds:F2}s (total {sw.Elapsed.TotalMinutes:F1}m / {timeoutSeconds / 60.0:F1}m)");
        }

        Log($"[done]   {iteration} iterations in {sw.Elapsed.TotalMinutes:F2} min; clean exit");
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

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  stress.exe <dump-path> [--timeout SECS] [--threads N] [--dt-reload-every N]");
        Console.Error.WriteLine("  stress.exe --validate <dump-path>");
    }
}
