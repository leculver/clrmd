using Microsoft.Diagnostics.Runtime.DacInterface;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DbgEngExtension
{
    public class MAddress : DbgEngCommand
    {
        public MAddress(nint pUnknown, bool redirectConsoleOutput = true)
            : base(pUnknown, redirectConsoleOutput)
        {
        }

        public MAddress(IDisposable dbgeng, bool redirectConsoleOutput = false)
            : base(dbgeng, redirectConsoleOutput)
        {
        }

        internal void Run(string args)
        {
            AddressMemoryRange[] ranges = EnumerateBangAddress().Where(m => m.State != MemState.MEM_FREE).OrderBy(r => r.Start).ToArray();
            foreach (ClrMemoryPointer mem in EnumerateClrMemoryAddresses())
            {
                var found = ranges.Where(m => m.Start <= mem.Address && mem.Address < m.End).ToArray();

                if (found.Length == 0)
                    Console.WriteLine($"Warning:  Could not find a memory range for {mem.Address:x} - {mem.Kind}.");
                else if (found.Length > 1)
                    Console.WriteLine($"Warning:  Found multiple memory ranges for entry {mem.Address:x} - {mem.Kind}.");

                foreach (var entry in found)
                {
                    if (entry.ClrMemoryKind != ClrMemoryKind.None && entry.ClrMemoryKind != mem.Kind)
                        Console.WriteLine($"Warning:  Overwriting range {entry.Start:x} {entry.ClrMemoryKind} -> {mem.Kind}.");

                    entry.ClrMemoryKind = mem.Kind;
                }
            }

            Console.WriteLine($"{"StartAddr",12}     {"EndAddr-1",12}     {"Type",12}     {"State",12}     {"Protect",20}     {"Usage"}");
            foreach (var mem in ranges)
            {
                string description = mem.Description;
                if (mem.ClrMemoryKind != ClrMemoryKind.None)
                    description = mem.ClrMemoryKind.ToString();

                Console.WriteLine($"{mem.Start,12:x}     {mem.End,12:x}     {mem.Kind,12}     {mem.State,12}     {mem.Protect,20}     {description}");
            }

            // Tag reserved memory based on what's adjacent.
            foreach (var mem in ranges.Where(r => GetMemoryName(r) == "RESERVED"))
                TagMemoryRecursive(mem, ranges);

            IEnumerable<AddressMemoryRange> rng = ranges;

            WriteSummaryTable(ranges);
            WriteSummaryTable(ranges.Where(r => r.State == MemState.MEM_COMMIT));

            var imageGroups = from mem in ranges.Where(r => r.State == MemState.MEM_COMMIT && r.Image != null)
                              group mem by mem.Image into g
                              let Size = g.Sum(k => (long)(k.End - k.Start))
                              orderby Size descending
                              select new
                              {
                                  Image = g.Key,
                                  Count = g.Count(),
                                  Size
                              };


            int count = 0;
            long size = 0;
            Console.WriteLine($"{"Image",32} {"Regions",12} {"Size (bytes)",12} {"Size",12}");
            foreach (var item in imageGroups)
            {
                Console.WriteLine($"{item.Image,32} {item.Count,12:n0} {item.Size,12:n0} {ConvertToHumanReadable(item.Size),12}");

                count += item.Count;
                size += item.Size;
            }

            Console.WriteLine($"{"TOTAL",32} {count,12:n0} {size,12:n0} {ConvertToHumanReadable(size),12}");
        }

        private static void WriteSummaryTable(IEnumerable<AddressMemoryRange> rng)
        {
            var grouped = from mem in rng
                          let type = GetMemoryName(mem)
                          group mem by type into g
                          let Count = g.Count()
                          let Size = g.Sum(f => (long)(f.End - f.Start))
                          orderby Size descending
                          select new
                          {
                              Type = g.Key,
                              Count,
                              Size
                          };



            Console.WriteLine("  ");
            Console.WriteLine($"{"TypeSummary",24}     {"RngCount",12:x}     {"Size (bytes)",12}     {"Size",12}");
            int count = 0;
            long size = 0;
            foreach (var item in grouped)
            {
                Console.WriteLine($"{item.Type,24}     {item.Count,12:n0}     {item.Size,12:n0}     {ConvertToHumanReadable(item.Size),12}");
                count += item.Count;
                size += item.Size;
            }

            Console.WriteLine($"{"TOTAL",24}     {count,12:n0}     {size,12:n0}     {ConvertToHumanReadable(size),12}");
        }

        private AddressMemoryRange? TagMemoryRecursive(AddressMemoryRange mem, AddressMemoryRange[] ranges)
        {
            string type = GetMemoryName(mem);
            if (type != "RESERVED")
                return mem;

            AddressMemoryRange? found = ranges.SingleOrDefault(r => r.End == mem.Start);
            if (found is null)
                return null;

            AddressMemoryRange? nonReserved = TagMemoryRecursive(found, ranges);
            if (nonReserved is null)
                return null;

            mem.Description = GetMemoryName(nonReserved);
            return nonReserved;
        }

        private static string GetMemoryName(AddressMemoryRange mem)
        {
            if (mem.ClrMemoryKind != ClrMemoryKind.None)
                return mem.ClrMemoryKind.ToString();

            if (!string.IsNullOrWhiteSpace(mem.Description))
                return mem.Description;

            if (mem.State == MemState.MEM_RESERVE)
                return "RESERVED";
            else if (mem.State == MemState.MEM_FREE)
                return "FREE";

            return mem.Protect.ToString();
        }

        private static string ConvertToHumanReadable(double totalBytes)
        {
            double updated = totalBytes;

            updated /= 1024;
            if (updated < 1024)
                return $"{updated:0.00}kb";

            updated /= 1024;
            if (updated < 1024)
                return $"{updated:0.00}mb";

            updated /= 1024;
            return $"{updated:0.00}gb";
        }

        public IEnumerable<ClrMemoryPointer> EnumerateClrMemoryAddresses()
        {
            foreach (var runtime in Runtimes)
            {
                SOSDac sos = runtime.DacLibrary.SOSDacInterface;
                foreach (JitManagerInfo jitMgr in sos.GetJitManagers())
                {
                    foreach (var mem in sos.GetCodeHeapList(jitMgr.Address))
                        yield return new ClrMemoryPointer() { Address = mem.Address, Kind = ConvertMemoryKind(mem.Type) };

                    foreach (var seg in runtime.Heap.Segments)
                    {
                        if (seg.CommittedMemory.Length > 0)
                            yield return new ClrMemoryPointer() { Address = seg.CommittedMemory.Start, Kind = ClrMemoryKind.GCHeapSegment };

                        if (seg.ReservedMemory.Length > 0)
                            yield return new ClrMemoryPointer() { Address = seg.ReservedMemory.Start, Kind = ClrMemoryKind.GCHeapReserve };
                    }

                    foreach (ClrDataAddress address in sos.GetAppDomainList())
                    {
                        List<ClrMemoryPointer> heaps = new List<ClrMemoryPointer>();
                        if (sos.GetAppDomainData(address, out AppDomainData domain))
                        {
                            sos.TraverseLoaderHeap(domain.StubHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.StubHeap
                            }));

                            sos.TraverseLoaderHeap(domain.HighFrequencyHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.HighFrequencyHeap
                            }));

                            sos.TraverseLoaderHeap(domain.LowFrequencyHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.LowFrequencyHeap
                            }));

                            sos.TraverseStubHeap(address, (int)VCSHeapType.IndcellHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.IndcellHeap
                            }));


                            sos.TraverseStubHeap(address, (int)VCSHeapType.LookupHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.LookupHeap
                            }));


                            sos.TraverseStubHeap(address, (int)VCSHeapType.ResolveHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.ResolveHeap
                            }));


                            sos.TraverseStubHeap(address, (int)VCSHeapType.DispatchHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.DispatchHeap
                            }));

                            sos.TraverseStubHeap(address, (int)VCSHeapType.CacheEntryHeap, (address, size, isCurrent) => heaps.Add(new ClrMemoryPointer()
                            {
                                Address = address,
                                Kind = ClrMemoryKind.CacheEntryHeap
                            }));
                        }

                        foreach (var heap in heaps)
                            yield return heap;
                    }
                }
            }
        }

        private ClrMemoryKind ConvertMemoryKind(CodeHeapType type)
        {
            return type switch
            {
                CodeHeapType.Loader => ClrMemoryKind.LoaderHeap,
                CodeHeapType.Host => ClrMemoryKind.Host,
                _ => ClrMemoryKind.UnknownCodeHeap
            };
        }

        public IEnumerable<AddressMemoryRange> EnumerateBangAddress()
        {
            bool foundHeader = false;
            bool skipped = false;

            (int hr, string text) = RunCommandWithOutput("!address");
            if (hr < 0)
                throw new InvalidOperationException($"!address failed with hresult={hr:x}");

            foreach (string line in text.Split('\n'))
            {
                if (line.Length == 0)
                    continue;

                if (!foundHeader)
                {
                    string[] split = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (split.Length > 0)
                        foundHeader = split[0] == "BaseAddress" && split.Last() == "Usage";
                }
                else if (!skipped)
                {
                    // skip the ---------- line
                    skipped = true;
                }
                else
                {
                    string[] parts = ((line[0] == '+') ? line[1..] : line).Split(new char[] { ' ' }, 6, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    ulong start = ulong.Parse(parts[0].Replace("`", ""), System.Globalization.NumberStyles.HexNumber);
                    ulong end = ulong.Parse(parts[1].Replace("`", ""), System.Globalization.NumberStyles.HexNumber);

                    int index = 3;
                    if (Enum.TryParse(parts[index], ignoreCase: true, out MemKind kind))
                        index++;

                    MemState state = Enum.Parse<MemState>(parts[index++]);

                    string remainder = index == 5 ? parts[5] : parts[4] + ' ' + parts[5];

                    parts = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    MemProtect protect = default;
                    index = 0;
                    while (index < parts.Length - 1)
                    {
                        if (Enum.TryParse(parts[index], ignoreCase: true, out MemProtect result))
                        {
                            protect |= result;
                            if (parts[index + 1] == "|")
                                index++;
                        }
                        else
                        {
                            break;
                        }

                        index++;
                    }

                    string description = parts[index++];

                    string? image = null;
                    if (kind == MemKind.MEM_IMAGE)
                    {
                        image = parts[index++][1..^1];
                    }
                        

                    if (description.Equals("<unknown>", StringComparison.OrdinalIgnoreCase))
                        description = "";

                    yield return new AddressMemoryRange()
                    {
                        Start = start,
                        End = end,
                        Kind = kind,
                        State = state,
                        Protect = protect,
                        Description = description,
                        Image = image
                    };
                }
            }

            if (!foundHeader)
                throw new InvalidOperationException($"!address did not produce a standard header.\nThis may mean symbols could not be resolved for ntdll.\nPlease run !address and make sure the output looks correct.");
        }

        public enum ClrMemoryKind
        {
            None,
            LoaderHeap,
            Host,
            UnknownCodeHeap,
            GCHeapSegment,
            GCHeapReserve,
            StubHeap,
            HighFrequencyHeap,
            LowFrequencyHeap,
            IndcellHeap,
            LookupHeap,
            ResolveHeap,
            DispatchHeap,
            CacheEntryHeap,
        }

        enum VCSHeapType
        {
            IndcellHeap,
            LookupHeap,
            ResolveHeap,
            DispatchHeap,
            CacheEntryHeap
        }

        [Flags]
        public enum MemProtect
        {
            PAGE_EXECUTE = 0x00000010,
            PAGE_EXECUTE_READ = 0x00000020,
            PAGE_EXECUTE_READWRITE = 0x00000040,
            PAGE_EXECUTE_WRITECOPY = 0x00000080,
            PAGE_NOACCESS = 0x00000001,
            PAGE_READONLY = 0x00000002,
            PAGE_READWRITE = 0x00000004,
            PAGE_WRITECOPY = 0x00000008,
            PAGE_GUARD = 0x00000100,
            PAGE_NOCACHE = 0x00000200,
            PAGE_WRITECOMBINE = 0x00000400
        }

        public enum MemState
        {
            MEM_COMMIT = 0x1000,
            MEM_FREE = 0x10000,
            MEM_RESERVE = 0x2000
        }

        public enum MemKind
        {
            MEM_IMAGE = 0x1000000,
            MEM_MAPPED = 0x40000,
            MEM_PRIVATE = 0x20000
        }

        public class AddressMemoryRange
        {
            public ulong Start { get; internal set; }
            public ulong End { get; internal set; }
            public MemKind Kind { get; internal set; }
            public MemState State { get; internal set; }
            public MemProtect Protect { get; internal set; }
            public string Description { get; internal set; } = "";
            public ClrMemoryKind ClrMemoryKind { get; internal set; }
            public string? Image { get; internal set; }
        }

        public class ClrMemoryPointer
        {
            public ulong Address { get; internal set; }
            public ClrMemoryKind Kind { get; internal set; }
        }
    }
}
