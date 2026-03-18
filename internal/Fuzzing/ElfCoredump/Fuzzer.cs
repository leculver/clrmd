using System;
using System.IO;
using Microsoft.Diagnostics.Runtime;
using SharpFuzz;

namespace Microsoft.Diagnostics.Runtime.Fuzzing.ElfCoredump;

public static class Program
{
    public static void Main()
    {
        Fuzzer.LibFuzzer.Run(FuzzTarget);
    }

    public static void FuzzTarget(ReadOnlySpan<byte> input)
    {
        try
        {
            using MemoryStream stream = new MemoryStream(input.ToArray());
            using DataTarget target = DataTarget.LoadDump("fuzz.core", stream, leaveOpen: true);

            _ = target.DataReader.Architecture;
            _ = target.DataReader.ProcessId;
            _ = target.DataReader.TargetPlatform;

            foreach (ModuleInfo module in target.EnumerateModules())
            {
                _ = module.FileName;
                _ = module.ImageBase;
                _ = module.ImageSize;
                _ = module.IndexFileSize;
                _ = module.IndexTimeStamp;
                _ = module.Version;
                _ = module.BuildId;
                _ = module.Pdb;
                _ = module.IsManaged;
                _ = module.Kind;
                _ = module.ToString();
            }

            foreach (ClrInfo clrInfo in target.ClrVersions)
            {
                _ = clrInfo.Version;
                _ = clrInfo.Flavor;
                _ = clrInfo.IsSingleFile;
                _ = clrInfo.ModuleInfo;
                _ = clrInfo.DebuggingLibraries;
                _ = clrInfo.IndexFileSize;
                _ = clrInfo.IndexTimeStamp;
                _ = clrInfo.BuildId;
                _ = clrInfo.ToString();
            }
        }
        catch (InvalidDataException)
        {
        }
        catch (BadImageFormatException)
        {
        }
        catch (IOException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (ArgumentException)
        {
        }
    }
}
