﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Microsoft.Diagnostics.Runtime.Utilities
{
    /// <summary>
    /// A class to read information out of PE images (dll/exe).
    /// </summary>
    public sealed unsafe class PEImage : IDisposable
    {
        private const ushort ExpectedDosHeaderMagic = 0x5A4D;   // MZ
        private const int PESignatureOffsetLocation = 0x3C;
        private const uint ExpectedPESignature = 0x00004550;    // PE00
        private const int ImageDataDirectoryCount = 15;
        private const int ComDataDirectory = 14;
        private const int DebugDataDirectory = 6;

        private readonly bool _virt;
        private int _offset = 0;
        private readonly int _peHeaderOffset;

        private readonly Lazy<ImageFileHeader?> _imageFileHeader;
        private readonly Lazy<ImageOptionalHeader?> _imageOptionalHeader;
        private readonly Lazy<CorHeader?> _corHeader;
        private readonly Lazy<List<SectionHeader>> _sections;
        private readonly Lazy<List<PdbInfo>> _pdbs;
        private readonly Lazy<IMAGE_DATA_DIRECTORY[]> _directories;
        private readonly Lazy<ResourceEntry> _resources;

        private IMAGE_DATA_DIRECTORY GetDirectory(int index) => _directories.Value[index];
        private int HeaderOffset => _peHeaderOffset + sizeof(uint);
        private int OptionalHeaderOffset => HeaderOffset + sizeof(IMAGE_FILE_HEADER);
        private int SpecificHeaderOffset => OptionalHeaderOffset + sizeof(IMAGE_OPTIONAL_HEADER_AGNOSTIC);
        private int DataDirectoryOffset => SpecificHeaderOffset + (IsPE64 ? 5 * 8 : 6 * 4);
        private int ImageDataDirectoryOffset => DataDirectoryOffset + ImageDataDirectoryCount * sizeof(IMAGE_DATA_DIRECTORY);

        /// <summary>
        /// Constructs a PEImage class for a given PE image (dll/exe) on disk.
        /// </summary>
        /// <param name="stream">A Stream that contains a PE image at its 0th offset.  This stream must be seekable.</param>
        public PEImage(Stream stream)
            : this(stream, false)
        {
        }

        /// <summary>
        /// Constructs a PEImage class for a given PE image (dll/exe) on disk.
        /// </summary>
        /// <param name="stream">A Stream that contains a PE image at its 0th offset.  This stream must be seekable.</param>
        /// <param name="isVirtual">Whether stream points to a PE image mapped into an address space (such as in a live process or crash dump).</param>
        public PEImage(Stream stream, bool isVirtual)
        {
            _virt = isVirtual;
            Stream = stream ?? throw new ArgumentNullException(nameof(stream));

            if (!stream.CanSeek)
                throw new ArgumentException($"{nameof(stream)} is not seekable.");

            ushort dosHeaderMagic = Read<ushort>(0);
            if (dosHeaderMagic != ExpectedDosHeaderMagic)
            {
                IsValid = false;
            }
            else
            {
                _peHeaderOffset = Read<int>(PESignatureOffsetLocation);

                uint peSignature = 0;
                if (_peHeaderOffset != 0)
                    peSignature = Read<uint>(_peHeaderOffset);

                IsValid = peSignature == ExpectedPESignature;
            }

            _imageFileHeader = new Lazy<ImageFileHeader?>(ReadImageFileHeader);
            _imageOptionalHeader = new Lazy<ImageOptionalHeader?>(ReadImageOptionalHeader);
            _corHeader = new Lazy<CorHeader?>(ReadCorHeader);
            _directories = new Lazy<IMAGE_DATA_DIRECTORY[]>(ReadDataDirectories);
            _sections = new Lazy<List<SectionHeader>>(ReadSections);
            _pdbs = new Lazy<List<PdbInfo>>(ReadPdbs);
            _resources = new Lazy<ResourceEntry>(CreateResourceRoot);
        }

        public void Dispose() { }

        internal int ResourceVirtualAddress => (int)GetDirectory(2).VirtualAddress;

        /// <summary>
        /// Gets the root resource node of this PEImage.
        /// </summary>
        public ResourceEntry Resources => _resources.Value;

        /// <summary>
        /// Gets the underlying stream.
        /// </summary>
        public Stream Stream { get; }

        /// <summary>
        /// Gets a value indicating whether the given Stream contains a valid DOS header and PE signature.
        /// </summary>
        public bool IsValid { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this image is for a 64bit processor.
        /// </summary>
        public bool IsPE64 => OptionalHeader != null && OptionalHeader.Magic != 0x010b;

        /// <summary>
        /// Gets a value indicating whether this image is managed. (.NET image)
        /// </summary>
        public bool IsManaged => OptionalHeader != null && OptionalHeader.ComDescriptorDirectory.VirtualAddress != 0;

        /// <summary>
        /// Gets the timestamp that this PE image is indexed under.
        /// </summary>
        public int IndexTimeStamp => Header?.TimeDateStamp ?? 0;

        /// <summary>
        /// Gets the file size that this PE image is indexed under.
        /// </summary>
        public int IndexFileSize => (int)(OptionalHeader?.SizeOfImage ?? 0);

        /// <summary>
        /// Gets the managed header information for this image.  Undefined behavior if IsValid is <see langword="false"/>.
        /// </summary>
        public CorHeader? CorHeader => _corHeader.Value;

        /// <summary>
        /// Gets a wrapper over this PE image's IMAGE_FILE_HEADER structure.  Undefined behavior if IsValid is <see langword="false"/>.
        /// </summary>
        public ImageFileHeader? Header => _imageFileHeader.Value;

        /// <summary>
        /// Gets a wrapper over this PE image's IMAGE_OPTIONAL_HEADER.  Undefined behavior if IsValid is <see langword="false"/>.
        /// </summary>
        public ImageOptionalHeader? OptionalHeader => _imageOptionalHeader.Value;

        /// <summary>
        /// Gets a collection of IMAGE_SECTION_HEADERs in the PE iamge.  Undefined behavior if IsValid is <see langword="false"/>.
        /// </summary>
        public ReadOnlyCollection<SectionHeader> Sections => _sections.Value.AsReadOnly();

        /// <summary>
        /// Gets a list of PDBs associated with this PE image.  PE images can contain multiple PDB entries,
        /// but by convention it's usually the last entry that is the most up to date.  Unless you need to enumerate
        /// all PDBs for some reason, you should use DefaultPdb instead.
        /// Undefined behavior if IsValid is <see langword="false"/>.
        /// </summary>
        public ReadOnlyCollection<PdbInfo> Pdbs => _pdbs.Value.AsReadOnly();

        /// <summary>
        /// Gets the PDB information for this module.  If this image does not contain PDB info (or that information
        /// wasn't included in Stream) this returns <see langword="null"/>.  If multiple PDB streams are present, this method returns the
        /// last entry.
        /// </summary>
        public PdbInfo DefaultPdb => Pdbs.LastOrDefault();

        /// <summary>
        /// Allows you to convert between a virtual address to a stream offset for this module.
        /// </summary>
        /// <param name="virtualAddress">The address to translate.</param>
        /// <returns>The position in the stream of the data, -1 if the virtual address doesn't map to any location of the PE image.</returns>
        public int RvaToOffset(int virtualAddress)
        {
            if (_virt)
                return virtualAddress;

            List<SectionHeader> sections = _sections.Value;
            for (int i = 0; i < sections.Count; i++)
                if (sections[i].VirtualAddress <= virtualAddress && virtualAddress < sections[i].VirtualAddress + sections[i].VirtualSize)
                    return (int)sections[i].PointerToRawData + (virtualAddress - (int)sections[i].VirtualAddress);

            return -1;
        }

        /// <summary>
        /// Reads data out of PE image into a native buffer.
        /// </summary>
        /// <param name="virtualAddress">The address to read from.</param>
        /// <param name="dest">The location to write the data.</param>
        /// <returns>The number of bytes actually read from the image and written to dest.</returns>
        public int Read(int virtualAddress, Span<byte> dest)
        {
            int offset = RvaToOffset(virtualAddress);
            if (offset == -1)
                return 0;

            SeekTo(offset);
            return Stream.Read(dest);
        }

        /// <summary>
        /// Gets the File Version Information that is stored as a resource in the PE file.  (This is what the
        /// version tab a file's property page is populated with).
        /// </summary>
        public FileVersionInfo? GetFileVersionInfo()
        {
            ResourceEntry? versionNode = Resources.Children.FirstOrDefault(r => r.Name == "Version");
            if (versionNode is null || versionNode.Children.Length != 1)
                return null;

            versionNode = versionNode.Children[0];
            if (!versionNode.IsLeaf && versionNode.Children.Length == 1)
                versionNode = versionNode.Children[0];

            int size = versionNode.Size;
            if (size < 16)  // Arbtirarily small value to ensure it's non-zero and has at least a little data in it
                return null;

            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                int count = versionNode.GetData(buffer);
                return new FileVersionInfo(buffer.AsSpan(0, count));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private ResourceEntry CreateResourceRoot()
        {
            return new ResourceEntry(this, null, "root", false, RvaToOffset(ResourceVirtualAddress));
        }

        private List<SectionHeader> ReadSections()
        {
            List<SectionHeader> sections = new List<SectionHeader>();
            if (!IsValid)
                return sections;

            ImageFileHeader? header = Header;
            if (header is null)
                return sections;

            SeekTo(ImageDataDirectoryOffset);

            // Sanity check, there's a null row at the end of the data directory table

            if (!TryRead(out ulong zero) || zero != 0)
                return sections;

            for (int i = 0; i < header.NumberOfSections; i++)
                if (TryRead(out IMAGE_SECTION_HEADER sectionHdr))
                    sections.Add(new SectionHeader(ref sectionHdr));

            return sections;
        }

        private List<PdbInfo> ReadPdbs()
        {
            int offs = _offset;
            List<PdbInfo> result = new List<PdbInfo>();

            var debugData = GetDirectory(DebugDataDirectory);
            if (debugData.VirtualAddress != 0 && debugData.Size != 0)
            {
                if ((debugData.Size % sizeof(IMAGE_DEBUG_DIRECTORY)) != 0)
                    return result;

                int offset = RvaToOffset((int)debugData.VirtualAddress);
                if (offset == -1)
                    return result;

                int count = (int)debugData.Size / sizeof(IMAGE_DEBUG_DIRECTORY);
                List<Tuple<int, int>> entries = new List<Tuple<int, int>>(count);

                SeekTo(offset);
                for (int i = 0; i < count; i++)
                {
                    if (TryRead(out IMAGE_DEBUG_DIRECTORY directory))
                    {
                        if (directory.Type == IMAGE_DEBUG_TYPE.CODEVIEW && directory.SizeOfData >= sizeof(CV_INFO_PDB70))
                            entries.Add(Tuple.Create(_virt ? directory.AddressOfRawData : directory.PointerToRawData, directory.SizeOfData));
                    }
                }

                foreach (Tuple<int, int> tmp in entries.OrderBy(e => e.Item1))
                {
                    int ptr = tmp.Item1;
                    int size = tmp.Item2;

                    if (TryRead(ptr, out int cvSig) && cvSig == CV_INFO_PDB70.PDB70CvSignature)
                    {
                        Guid guid = Read<Guid>();
                        int age = Read<int>();

                        // sizeof(sig) + sizeof(guid) + sizeof(age) - [null char] = 0x18 - 1
                        int nameLen = size - 0x18 - 1;
                        string? path = ReadString(nameLen);

                        if (path != null)
                        {
                            PdbInfo pdb = new PdbInfo(path, guid, age);
                            result.Add(pdb);
                        }
                    }
                }
            }

            return result;
        }

        private string? ReadString(int len) => ReadString(_offset, len);

        private string? ReadString(int offset, int len)
        {
            if (len > 4096)
                len = 4096;

            SeekTo(offset);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(len);
            try
            {
                if (Stream.Read(buffer, 0, len) != len)
                    return null;

                int index = Array.IndexOf(buffer, (byte)'\0', 0, len);
                if (index >= 0)
                    len = index;

                return Encoding.ASCII.GetString(buffer, 0, len);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private bool TryRead<T>(out T result) where T : unmanaged => TryRead(_offset, out result);

        private bool TryRead<T>(int offset, out T t) where T : unmanaged
        {
            t = default;
            int size = Unsafe.SizeOf<T>();

            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
            try
            {
                SeekTo(offset);
                int read = Stream.Read(buffer, 0, size);
                _offset = offset + read;

                if (read != size)
                    return false;

                t = Unsafe.As<byte, T>(ref buffer[0]);

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        internal T Read<T>(int offset) where T : unmanaged => Read<T>(ref offset);

        internal T Read<T>(ref int offset) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            T t = default;

            SeekTo(offset);
            int read = Stream.Read(new Span<byte>(&t, size));
            offset += read;
            _offset = offset;

            if (read != size)
                return default;
            return t;
        }

        internal T Read<T>() where T : unmanaged => Read<T>(_offset);

        private void SeekTo(int offset)
        {
            if (offset != _offset)
            {
                Stream.Seek(offset, SeekOrigin.Begin);
                _offset = offset;
            }
        }

        private ImageFileHeader? ReadImageFileHeader()
        {
            if (!IsValid)
                return null;

            if (TryRead(HeaderOffset, out IMAGE_FILE_HEADER header))
                return new ImageFileHeader(ref header);

            return null;
        }

        private IMAGE_DATA_DIRECTORY[] ReadDataDirectories()
        {
            IMAGE_DATA_DIRECTORY[] directories = new IMAGE_DATA_DIRECTORY[ImageDataDirectoryCount];

            if (!IsValid)
                return directories;

            SeekTo(DataDirectoryOffset);
            for (int i = 0; i < directories.Length; i++)
                directories[i] = Read<IMAGE_DATA_DIRECTORY>();

            return directories;
        }

        private ImageOptionalHeader? ReadImageOptionalHeader()
        {
            if (!IsValid)
                return null;

            if (!TryRead(OptionalHeaderOffset, out IMAGE_OPTIONAL_HEADER_AGNOSTIC optional))
                return null;

            bool is32Bit = optional.Magic == 0x010b;
            Lazy<IMAGE_OPTIONAL_HEADER_SPECIFIC> specific = new Lazy<IMAGE_OPTIONAL_HEADER_SPECIFIC>(() =>
            {
                SeekTo(SpecificHeaderOffset);
                return new IMAGE_OPTIONAL_HEADER_SPECIFIC()
                {
                    SizeOfStackReserve = is32Bit ? Read<uint>() : Read<ulong>(),
                    SizeOfStackCommit = is32Bit ? Read<uint>() : Read<ulong>(),
                    SizeOfHeapReserve = is32Bit ? Read<uint>() : Read<ulong>(),
                    SizeOfHeapCommit = is32Bit ? Read<uint>() : Read<ulong>(),
                    LoaderFlags = Read<uint>(),
                    NumberOfRvaAndSizes = Read<uint>()
                };
            });

            return new ImageOptionalHeader(ref optional, specific, _directories, is32Bit);
        }

        private CorHeader? ReadCorHeader()
        {
            var clrDataDirectory = GetDirectory(ComDataDirectory);

            int offset = RvaToOffset((int)clrDataDirectory.VirtualAddress);
            if (offset == -1)
                return null;

            if (TryRead(offset, out IMAGE_COR20_HEADER hdr))
                return new CorHeader(ref hdr);

            return null;
        }
    }
}
