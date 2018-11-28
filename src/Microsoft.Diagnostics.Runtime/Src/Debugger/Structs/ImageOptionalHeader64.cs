﻿using System;
using System.Runtime.InteropServices;

namespace Microsoft.Diagnostics.Runtime.Interop
{
    [StructLayout(LayoutKind.Explicit)]
    public struct IMAGE_OPTIONAL_HEADER64
    {
        [FieldOffset(0)]
        public ushort Magic;
        [FieldOffset(2)]
        public byte MajorLinkerVersion;
        [FieldOffset(3)]
        public byte MinorLinkerVersion;
        [FieldOffset(4)]
        public UInt32 SizeOfCode;
        [FieldOffset(8)]
        public UInt32 SizeOfInitializedData;
        [FieldOffset(12)]
        public UInt32 SizeOfUninitializedData;
        [FieldOffset(16)]
        public UInt32 AddressOfEntryPoint;
        [FieldOffset(20)]
        public UInt32 BaseOfCode;
        [FieldOffset(24)]
        public UInt64 ImageBase;
        [FieldOffset(32)]
        public UInt32 SectionAlignment;
        [FieldOffset(36)]
        public UInt32 FileAlignment;
        [FieldOffset(40)]
        public ushort MajorOperatingSystemVersion;
        [FieldOffset(42)]
        public ushort MinorOperatingSystemVersion;
        [FieldOffset(44)]
        public ushort MajorImageVersion;
        [FieldOffset(46)]
        public ushort MinorImageVersion;
        [FieldOffset(48)]
        public ushort MajorSubsystemVersion;
        [FieldOffset(50)]
        public ushort MinorSubsystemVersion;
        [FieldOffset(52)]
        public UInt32 Win32VersionValue;
        [FieldOffset(56)]
        public UInt32 SizeOfImage;
        [FieldOffset(60)]
        public UInt32 SizeOfHeaders;
        [FieldOffset(64)]
        public UInt32 CheckSum;
        [FieldOffset(68)]
        public ushort Subsystem;
        [FieldOffset(70)]
        public ushort DllCharacteristics;
        [FieldOffset(72)]
        public UInt64 SizeOfStackReserve;
        [FieldOffset(80)]
        public UInt64 SizeOfStackCommit;
        [FieldOffset(88)]
        public UInt64 SizeOfHeapReserve;
        [FieldOffset(96)]
        public UInt64 SizeOfHeapCommit;
        [FieldOffset(104)]
        public UInt32 LoaderFlags;
        [FieldOffset(108)]
        public UInt32 NumberOfRvaAndSizes;
        [FieldOffset(112)]
        public IMAGE_DATA_DIRECTORY DataDirectory0;
        [FieldOffset(120)]
        public IMAGE_DATA_DIRECTORY DataDirectory1;
        [FieldOffset(128)]
        public IMAGE_DATA_DIRECTORY DataDirectory2;
        [FieldOffset(136)]
        public IMAGE_DATA_DIRECTORY DataDirectory3;
        [FieldOffset(144)]
        public IMAGE_DATA_DIRECTORY DataDirectory4;
        [FieldOffset(152)]
        public IMAGE_DATA_DIRECTORY DataDirectory5;
        [FieldOffset(160)]
        public IMAGE_DATA_DIRECTORY DataDirectory6;
        [FieldOffset(168)]
        public IMAGE_DATA_DIRECTORY DataDirectory7;
        [FieldOffset(176)]
        public IMAGE_DATA_DIRECTORY DataDirectory8;
        [FieldOffset(184)]
        public IMAGE_DATA_DIRECTORY DataDirectory9;
        [FieldOffset(192)]
        public IMAGE_DATA_DIRECTORY DataDirectory10;
        [FieldOffset(200)]
        public IMAGE_DATA_DIRECTORY DataDirectory11;
        [FieldOffset(208)]
        public IMAGE_DATA_DIRECTORY DataDirectory12;
        [FieldOffset(216)]
        public IMAGE_DATA_DIRECTORY DataDirectory13;
        [FieldOffset(224)]
        public IMAGE_DATA_DIRECTORY DataDirectory14;
        [FieldOffset(232)]
        public IMAGE_DATA_DIRECTORY DataDirectory15;

        public static unsafe IMAGE_DATA_DIRECTORY* GetDataDirectory(IMAGE_OPTIONAL_HEADER64* header, int zeroBasedIndex)
        {
            return (&header->DataDirectory0) + zeroBasedIndex;
        }
    }
}