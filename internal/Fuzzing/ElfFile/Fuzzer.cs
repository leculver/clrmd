using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;
using SharpFuzz;

namespace Microsoft.Diagnostics.Runtime.Fuzzing.ElfFile;

public static class Program
{
    public static void Main()
    {
        Fuzzer.LibFuzzer.Run(FuzzTarget);
    }

    public static void FuzzTarget(ReadOnlySpan<byte> input)
    {
        if (input.Length < 4)
            return;

        try
        {
            using MemoryStream stream = new MemoryStream(input.ToArray());
            using Utilities.ElfFile elf = new Utilities.ElfFile(stream, leaveOpen: true);

            // Exercise header parsing
            _ = elf.Header;
            _ = elf.Header.Is64Bit;
            _ = elf.Header.Architecture;
            _ = elf.Header.ProgramHeaderCount;
            _ = elf.Header.ProgramHeaderOffset;
            _ = elf.Header.ProgramHeaderEntrySize;

            // Exercise program header parsing
            try
            {
                foreach (ElfProgramHeader ph in elf.ProgramHeaders)
                {
                    _ = ph.Type;
                    _ = ph.VirtualAddress;
                    _ = ph.VirtualSize;
                    _ = ph.FileOffset;
                    _ = ph.FileSize;
                }
            }
            catch (InvalidDataException) { }

            // Exercise notes parsing (build IDs, etc.)
            try
            {
                foreach (ElfNote note in elf.Notes)
                {
                    _ = note.Type;
                    _ = note.Name;
                    _ = note.Header;
                    _ = note.TotalSize;
                }
            }
            catch (InvalidDataException) { }
            catch (IOException) { }

            // Exercise build ID extraction
            try
            {
                _ = elf.BuildId;
            }
            catch (InvalidDataException) { }
            catch (IOException) { }

            // Exercise dynamic section / symbol lookup
            try
            {
                _ = elf.TryGetExportSymbol("_init", out _);
                _ = elf.TryGetExportSymbol("main", out _);
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
    }
}
