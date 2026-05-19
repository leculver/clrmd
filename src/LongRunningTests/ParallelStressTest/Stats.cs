// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace ClrMD.Stress;

/// <summary>
/// Running counters for a single stress.exe invocation. Workers increment
/// per-kind counters with <see cref="Interlocked"/>. On clean exit OR FailFast,
/// <see cref="WriteStatsLine"/> appends one JSONL record so the outer loop can
/// roll up total coverage across all invocations.
/// </summary>
internal static class Stats
{
    public static string? StatsFile;
    public static string DumpPath = "<unknown>";
    public static int ThreadCount;
    public static Stopwatch StartTime = Stopwatch.StartNew();

    public static long Iterations;
    public static long DataTargetReloads;

    // Per-worker-kind progress counters.
    public static long HeapObjectsWalked;     // count of objects yielded by EnumerateObjects across all heap workers
    public static long RootsWalked;           // count of roots yielded by EnumerateRoots across all root workers
    public static long BfsReachableVisited;   // sum of seen.Count over all BFS worker completions
    public static long BfsRefsExpanded;       // sum of EnumerateReferences calls across all BFS worker iterations

    // Outcomes
    public static int HeapWorkerRuns;
    public static int RootWorkerRuns;
    public static int BfsWorkerRuns;

    private static readonly object s_writeSync = new();

    public static void WriteStatsLine(string status, string? reason = null)
    {
        if (StatsFile is null) return;

        // JSONL: one record per stress.exe invocation
        string ts = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        double durSec = StartTime.Elapsed.TotalSeconds;

        System.Text.StringBuilder sb = new();
        sb.Append('{');
        sb.Append("\"ts\":\""); sb.Append(ts); sb.Append('"');
        sb.Append(",\"pid\":"); sb.Append(Environment.ProcessId);
        sb.Append(",\"dump\":\""); sb.Append(JsonEscape(DumpPath)); sb.Append('"');
        sb.Append(",\"status\":\""); sb.Append(JsonEscape(status)); sb.Append('"');
        if (reason is not null)
        {
            sb.Append(",\"reason\":\""); sb.Append(JsonEscape(reason)); sb.Append('"');
        }
        sb.Append(",\"durationSec\":"); sb.Append(durSec.ToString("F2", CultureInfo.InvariantCulture));
        sb.Append(",\"threads\":"); sb.Append(ThreadCount);
        sb.Append(",\"iterations\":"); sb.Append(Iterations);
        sb.Append(",\"dataTargetReloads\":"); sb.Append(DataTargetReloads);
        sb.Append(",\"heapObjectsWalked\":"); sb.Append(HeapObjectsWalked);
        sb.Append(",\"rootsWalked\":"); sb.Append(RootsWalked);
        sb.Append(",\"bfsReachableVisited\":"); sb.Append(BfsReachableVisited);
        sb.Append(",\"bfsRefsExpanded\":"); sb.Append(BfsRefsExpanded);
        sb.Append(",\"heapWorkerRuns\":"); sb.Append(HeapWorkerRuns);
        sb.Append(",\"rootWorkerRuns\":"); sb.Append(RootWorkerRuns);
        sb.Append(",\"bfsWorkerRuns\":"); sb.Append(BfsWorkerRuns);
        sb.Append('}');
        sb.Append('\n');

        // Best-effort: multiple stress.exe processes can write concurrently to the same
        // file, so use FileShare.ReadWrite and a process-wide lock for our own writes.
        // Different processes serialize via OS-level file locking on Windows.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                lock (s_writeSync)
                {
                    using FileStream fs = new(StatsFile, FileMode.Append, FileAccess.Write, FileShare.Read);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                    fs.Write(bytes, 0, bytes.Length);
                }
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(25);   // another process may hold the file briefly; retry
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[stats] failed to write stats line: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }
    }

    private static string JsonEscape(string s)
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
}
