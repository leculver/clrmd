// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace ClrMD.Stress;

/// <summary>
/// Centralized failure reporting for the stress test. Any inconsistency or
/// unexpected exception observed by a worker funnels through here. We log
/// rich diagnostic context to stderr (under a lock so messages from multiple
/// threads don't interleave) and then call <see cref="Environment.FailFast"/>
/// so the .NET dump-collection environment variables capture a full crash dump.
/// </summary>
internal static class Failure
{
    private static readonly object s_sync = new();

    /// <summary>The dump path this stress.exe invocation is exercising. Used in every failure log line.</summary>
    public static string DumpPath { get; set; } = "<unknown-dump>";

    /// <summary>Current outer-loop iteration. Updated by Program before each parallel run.</summary>
    public static int CurrentIteration;

    public static void Fail(string workerKind, int? clrIndex, string reason, Exception? ex = null)
    {
        string clr = clrIndex is null ? "?" : clrIndex.Value.ToString();
        lock (s_sync)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("================ STRESS-FAIL ================");
            Console.Error.WriteLine($"  dump:      {DumpPath}");
            Console.Error.WriteLine($"  iteration: {CurrentIteration}");
            Console.Error.WriteLine($"  worker:    {workerKind}");
            Console.Error.WriteLine($"  clr-index: {clr}");
            Console.Error.WriteLine($"  thread:    {Environment.CurrentManagedThreadId}");
            Console.Error.WriteLine($"  reason:    {reason}");
            if (ex is not null)
            {
                Console.Error.WriteLine("  exception:");
                Console.Error.WriteLine(ex.ToString());
            }
            Console.Error.WriteLine("=============================================");
            Console.Error.Flush();
        }

        // FailFast unwinds the process and triggers the DOTNET_DbgEnableMiniDump
        // collector configured by the outer script. Pass the exception so it
        // ends up in the WER/createdump report when available.
        if (Debugger.IsAttached)
            Debugger.Break();

        // Persist coverage for this invocation BEFORE the process aborts.
        Stats.WriteStatsLine("failed", $"[{workerKind}] {reason}");

        Environment.FailFast($"ClrMD stress test failure [{workerKind}] {reason}", ex);
    }
}
