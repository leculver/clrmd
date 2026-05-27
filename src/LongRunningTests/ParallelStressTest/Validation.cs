// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Runtime;

namespace ClrMD.Stress;

/// <summary>
/// stress.exe --validate &lt;dump&gt; mode. Performs a single-threaded load of the
/// dump (using the lock-free MMF reader), then for each <see cref="ClrInfo"/>
/// tries to create a runtime and execute one heap walk + one root walk + one
/// BFS reachability walk.
///
/// Exit codes:
///   0 - at least one CLR worked; per-CLR counts printed to stdout as JSON
///   2 - no CLRs worked; outer script should DELETE the dump
///   3 - OutOfMemoryException during validation; outer script should
///       blocklist the dump WITHOUT deleting it
///   4 - the dump file could not even be opened (corrupted / unknown format).
///       Outer script should delete the dump.
/// </summary>
internal static class Validation
{
    public static int Run(string dumpPath)
    {
        DataTargetOptions opts = new()
        {
            UseLockFreeMemoryMapReader = false,
            VerifyDacOnWindows = false,
        };

        DataTarget dt;
        try
        {
            dt = DataTarget.LoadDump(dumpPath, opts);
        }
        catch (OutOfMemoryException)
        {
            EmitJson(dumpPath, status: "oom-on-load", clrs: Array.Empty<ClrResult>());
            return 3;
        }
        catch (Exception ex)
        {
            EmitJson(dumpPath, status: $"load-failed:{ex.GetType().Name}:{Escape(ex.Message)}", clrs: Array.Empty<ClrResult>());
            return 4;
        }

        using (dt)
        {
            List<ClrResult> results = new();
            int workingCount = 0;
            bool sawOom = false;

            for (int i = 0; i < dt.ClrVersions.Length; i++)
            {
                ClrInfo info = dt.ClrVersions[i];
                ClrResult r = ValidateOne(dt, i, info, ref sawOom);
                results.Add(r);
                if (r.Ok) workingCount++;
            }

            EmitJson(dumpPath, status: workingCount > 0 ? "ok" : (sawOom ? "oom" : "no-working-clr"), clrs: results.ToArray());

            if (workingCount > 0) return 0;
            if (sawOom) return 3;
            return 2;
        }
    }

    private static ClrResult ValidateOne(DataTarget dt, int index, ClrInfo info, ref bool sawOom)
    {
        try
        {
            using ClrRuntime runtime = info.CreateRuntime();
            ClrHeap heap = runtime.Heap;

            int objCount = heap.EnumerateObjects().Count();
            int rootCount = 0;
            List<ulong> rootAddrs = new();
            foreach (ClrRoot root in heap.EnumerateRoots())
            {
                rootCount++;
                if (root.Object.Address != 0)
                    rootAddrs.Add(root.Object.Address);
            }

            ulong[] uniqRoots = rootAddrs.Distinct().ToArray();
            int reachable = BfsReachability.Walk(heap, uniqRoots);

            return new ClrResult
            {
                Index = index,
                Flavor = info.Flavor.ToString(),
                Version = info.Version.ToString(),
                Ok = true,
                Objects = objCount,
                Roots = rootCount,
                Reachable = reachable,
                Error = null,
            };
        }
        catch (OutOfMemoryException ex)
        {
            sawOom = true;
            return new ClrResult { Index = index, Flavor = info.Flavor.ToString(), Version = info.Version.ToString(), Ok = false, Error = "oom:" + Escape(ex.Message) };
        }
        catch (Exception ex)
        {
            return new ClrResult { Index = index, Flavor = info.Flavor.ToString(), Version = info.Version.ToString(), Ok = false, Error = ex.GetType().Name + ":" + Escape(ex.Message) };
        }
    }

    private static void EmitJson(string dumpPath, string status, ClrResult[] clrs)
    {
        // Hand-rolled JSON to avoid pulling in System.Text.Json dependencies and to keep output stable.
        Console.Write("{\"dump\":\"");
        Console.Write(Escape(dumpPath));
        Console.Write("\",\"status\":\"");
        Console.Write(status);
        Console.Write("\",\"clrs\":[");
        for (int i = 0; i < clrs.Length; i++)
        {
            if (i > 0) Console.Write(",");
            ClrResult c = clrs[i];
            Console.Write("{\"index\":");
            Console.Write(c.Index);
            Console.Write(",\"flavor\":\"");
            Console.Write(Escape(c.Flavor ?? ""));
            Console.Write("\",\"version\":\"");
            Console.Write(Escape(c.Version ?? ""));
            Console.Write("\",\"ok\":");
            Console.Write(c.Ok ? "true" : "false");
            if (c.Ok)
            {
                Console.Write(",\"objects\":");
                Console.Write(c.Objects);
                Console.Write(",\"roots\":");
                Console.Write(c.Roots);
                Console.Write(",\"reachable\":");
                Console.Write(c.Reachable);
            }
            else if (c.Error is not null)
            {
                Console.Write(",\"error\":\"");
                Console.Write(Escape(c.Error));
                Console.Write("\"");
            }
            Console.Write("}");
        }
        Console.WriteLine("]}");
        Console.Out.Flush();
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        System.Text.StringBuilder sb = new(s.Length);
        foreach (char ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append($"\\u{(int)ch:x4}");
                    else sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private sealed class ClrResult
    {
        public int Index { get; init; }
        public string? Flavor { get; init; }
        public string? Version { get; init; }
        public bool Ok { get; init; }
        public int Objects { get; init; }
        public int Roots { get; init; }
        public int Reachable { get; init; }
        public string? Error { get; init; }
    }
}
