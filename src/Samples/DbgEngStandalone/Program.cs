// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using DbgEngExtension;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;
using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;

if (args.Length != 2 || !File.Exists(args[0]))
    Exit("Usage:  DbgEngStandalone [dump-file-path]");

// There is a copy of dbgeng.dll in the System32 folder, but that copy of DbgEng is very old
// and has several features compiled out of it for security reasons.  We can use that version
// but it limits functionality.  Instead, you can set a path (or an EnvVariable) to contain
// a locally installed copy of DbgEng.  This demo will still work if we default to the
// system32 version, but it works better with an installed version of DbgEng.
const string ExpectedDbgEngInstallPath = @"d:\amd64";

// This isn't set by anything, but you can set it yourself for this demo.
const string ExpectedDbgEngPathEnvVariable = "DbgEngPath";

string? dbgengPath = FindDbgEngPath();

// IDebugClient.Create creates a COM wrapper object.  You can cast this object to dbgeng interfaces.
using IDisposable dbgeng = IDebugClient.Create(dbgengPath);

// All DbgEng interfaces are simply
IDebugClient client = (IDebugClient)dbgeng;
IDebugControl control = (IDebugControl)dbgeng;

// Launch the target program in a new process
Dictionary<string, string> env = new()
{
    { "DOTNET_ROOT", args[1] },
    { "DOTNET_EnableWriteXorExecute", "0" },
    { "DOTNET_ENABLED_SOS_LOGGING", @"d:\work\sos.log" }
};
DEBUG_CREATE_PROCESS_OPTIONS opts = new()
{
    CreateFlags = DEBUG_CREATE_PROCESS.DEBUG_PROCESS
};

HResult hr = client.CreateProcessAndAttach(args[0], Path.GetDirectoryName(args[0]), env, DEBUG_ATTACH.DEFAULT, opts);
CheckHResult(hr, $"Failed to load {args[0]}.");

hr = control.WaitForEvent(TimeSpan.MaxValue);
CheckHResult(hr, "WaitForEvent unexpectedly failed.");

using DbgEngOutputHolder output = new(client, DEBUG_OUTPUT.ALL);
output.OutputReceived += (text, flags) => {
    ConsoleColor oldColor = Console.ForegroundColor;

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write($"[{flags}] ");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write(text);
    Console.Out.Flush();

    Console.ForegroundColor = oldColor;
};

hr = control.Execute(DEBUG_OUTCTL.THIS_CLIENT, ".chain", DEBUG_EXECUTE.DEFAULT);

MainLoop(control, out hr);

// End of Demo.



// Helper Methods
static void CheckHResult(HResult hr, string msg)
{
    if (!hr)
        Exit(msg, hr);
}

static void Exit(string message, int exitCode = -1)
{
    Console.Error.WriteLine(message);
    Environment.Exit(exitCode);
}

static string? FindDbgEngPath(string? hint = null)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        throw new NotSupportedException($"DbgEng only exists for Windows.");

    if (CheckOneFolderForDbgEng(hint))
        return hint;

    if (CheckOneFolderForDbgEng(ExpectedDbgEngInstallPath))
        return ExpectedDbgEngInstallPath;

    string? dbgEngEnv = Environment.GetEnvironmentVariable(ExpectedDbgEngPathEnvVariable);
    if (CheckOneFolderForDbgEng(dbgEngEnv))
        return dbgEngEnv;

    string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    if (CheckOneFolderForDbgEng(system32))
        return system32;

    return null;
}

static bool CheckOneFolderForDbgEng([NotNullWhen(true)] string? directory)
{
    if (!Directory.Exists(directory))
        return false;

    string path = Path.Combine(directory, "dbgeng.dll");
    return File.Exists(path);
}

static void MainLoop(IDebugControl control, out HResult hr)
{
    while (true)
    {
        // Wait for a debug event
        hr = control.WaitForEvent(TimeSpan.MaxValue);

        if (hr == HResult.S_OK)
        {
            // Retrieve the event type
            control.GetLastEventInformation(out DEBUG_EVENT eventType, out int pid, out int tid);

            switch (eventType)
            {
                case DEBUG_EVENT.BREAKPOINT:
                    Console.WriteLine("Breakpoint hit");

                    // Continue execution
                    hr = control.SetExecutionStatus(DEBUG_STATUS.GO);
                    if (!hr)
                    {
                        Console.WriteLine("Failed to continue execution");
                        return;
                    }

                    break;

                case DEBUG_EVENT.EXIT_PROCESS:
                    Console.WriteLine("Process exited");
                    return;

                default:
                    Console.WriteLine($"Other event: {eventType}");

                    // Continue execution
                    hr = control.SetExecutionStatus(DEBUG_STATUS.GO);
                    if (!hr)
                        return;

                    break;
            }
        }
    }
}