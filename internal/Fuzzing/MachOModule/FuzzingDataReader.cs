using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Runtime;

namespace Microsoft.Diagnostics.Runtime.Fuzzing.MachOModule;

/// <summary>
/// Minimal IDataReader that wraps a byte array for fuzzing.
/// MachOModule reads structured data at offsets via Read&lt;T&gt; and Read(address, buffer).
/// </summary>
internal sealed class FuzzingDataReader : CommonMemoryReader, IDataReader
{
    private readonly byte[] _data;

    public string DisplayName => "Fuzzing";
    public bool IsThreadSafe => false;
    public OSPlatform TargetPlatform => OSPlatform.OSX;
    public Architecture Architecture => Architecture.Arm64;
    public int ProcessId => 0;

    public FuzzingDataReader(byte[] data)
    {
        _data = data;
        PointerSize = 8;
    }

    public override int PointerSize { get; }

    public override int Read(ulong address, Span<byte> buffer)
    {
        if (address >= (ulong)_data.Length)
            return 0;

        int available = _data.Length - (int)address;
        int count = Math.Min(buffer.Length, available);
        if (count <= 0)
            return 0;

        _data.AsSpan((int)address, count).CopyTo(buffer);
        return count;
    }

    public IEnumerable<ModuleInfo> EnumerateModules() => Enumerable.Empty<ModuleInfo>();
    public bool GetThreadContext(uint threadID, uint contextFlags, Span<byte> context) => false;
    public void FlushCachedData() { }
}
