using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.MacOS;
using SharpFuzz;

namespace Microsoft.Diagnostics.Runtime.Fuzzing.MachOModule;

public static class Program
{
    public static void Main()
    {
        Fuzzer.LibFuzzer.Run(FuzzTarget);
    }

    public static void FuzzTarget(ReadOnlySpan<byte> input)
    {
        if (input.Length < 32)
            return;

        try
        {
            byte[] data = input.ToArray();
            FuzzingDataReader reader = new FuzzingDataReader(data);

            MacOS.MachOModule module = new MacOS.MachOModule(
                reader,
                address: 0,
                path: "fuzz.dylib",
                limits: new DataTargetLimits());

            // Exercise header-derived properties
            _ = module.BaseAddress;
            _ = module.ImageSize;
            _ = module.FileName;
            _ = module.LoadBias;

            // Exercise build ID (UUID load command)
            try
            {
                _ = module.BuildId;
            }
            catch (InvalidDataException) { }

            // Exercise segment enumeration
            try
            {
                foreach (var seg in module.EnumerateSegments())
                {
                    _ = seg.Name;
                    _ = seg.VMAddr;
                    _ = seg.VMSize;
                    _ = seg.FileOffset;
                    _ = seg.FileSize;
                }
            }
            catch (InvalidDataException) { }

            // Exercise symbol lookup
            try
            {
                _ = module.TryLookupSymbol("_main", out _);
                _ = module.TryLookupSymbol("_init", out _);
                _ = module.TryLookupSymbol("dyld_all_image_infos", out _);
            }
            catch (InvalidDataException) { }
            catch (IOException) { }
        }
        catch (InvalidDataException) { }
        catch (BadImageFormatException) { }
        catch (IOException) { }
        catch (NotSupportedException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        catch (OverflowException) { }
        catch (OutOfMemoryException) { }
    }
}
