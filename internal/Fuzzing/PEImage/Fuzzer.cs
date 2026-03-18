using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;
using SharpFuzz;

namespace Microsoft.Diagnostics.Runtime.Fuzzing.PEImage;

public static class Program
{
    public static void Main()
    {
        Fuzzer.LibFuzzer.Run(FuzzTarget);
    }

    public static void FuzzTarget(ReadOnlySpan<byte> input)
    {
        if (input.Length < 2)
            return;

        try
        {
            // Test PE parsing with file layout (no relocations)
            using MemoryStream stream = new MemoryStream(input.ToArray());
            using Utilities.PEImage pe = new Utilities.PEImage(
                stream,
                leaveOpen: true,
                isVirtual: false,
                loadedImageBase: 0,
                limits: new DataTargetLimits());

            if (!pe.IsValid)
                return;

            // Exercise header parsing
            _ = pe.IsPE64;
            _ = pe.IsManaged;
            _ = pe.IndexTimeStamp;
            _ = pe.IndexFileSize;

            // Exercise debug directory parsing (PDB info)
            try
            {
                _ = pe.Pdbs;
                _ = pe.DefaultPdb;
            }
            catch (InvalidDataException) { }

            // Exercise resource tree + version info parsing
            try
            {
                _ = pe.Resources;
                _ = pe.GetFileVersionInfo();
            }
            catch (InvalidDataException) { }
            catch (OverflowException) { }

            // Exercise export table parsing
            try
            {
                _ = pe.TryGetExportSymbol("CLRDataCreateInstance", out _);
                _ = pe.TryGetExportSymbol("DllGetClassObject", out _);
            }
            catch (InvalidDataException) { }
        }
        catch (InvalidDataException) { }
        catch (BadImageFormatException) { }
        catch (IOException) { }
        catch (NotSupportedException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        catch (OverflowException) { }
        catch (OutOfMemoryException) { }

        // Also test with relocation processing (loadedImageBase != 0)
        try
        {
            using MemoryStream stream2 = new MemoryStream(input.ToArray());
            using Utilities.PEImage peReloc = new Utilities.PEImage(
                stream2,
                leaveOpen: true,
                isVirtual: false,
                loadedImageBase: 0x10000,
                limits: new DataTargetLimits());

            if (peReloc.IsValid)
            {
                _ = peReloc.IsPE64;
                _ = peReloc.IsManaged;
                try { _ = peReloc.Pdbs; } catch (InvalidDataException) { }
            }
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
