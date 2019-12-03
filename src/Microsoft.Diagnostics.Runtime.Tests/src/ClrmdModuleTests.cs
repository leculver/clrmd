using Microsoft.Diagnostics.Runtime.DacInterface;
using Microsoft.Diagnostics.Runtime.Implementation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class ClrmdModuleTests : IModuleData, IModuleHelpers
    {
        [Fact]
        public void ModulePropertyTest()
        {
            // Ensure that IModuleData properties are respected by ClrmdModule.
            ClrmdModule module = new ClrmdModule(null, this);

            Assert.Equal(Address, module.Address);
            Assert.Equal(IsPEFile, module.IsPEFile);
            Assert.Equal(IsReflection, module.IsDynamic);
            Assert.Equal(ILImageBase, module.ImageBase);
            Assert.Equal(Size, module.Size);
            Assert.Equal(MetadataStart, module.MetadataAddress);
            Assert.Equal(MetadataLength, module.MetadataLength);
            Assert.Equal(Name, module.Name);
            Assert.Equal(AssemblyName, module.AssemblyName);

            (ulong, uint)[] tokenMap = module.EnumerateTypeDefToMethodTableMap().ToArray();
            Assert.Equal((1ul, 1u), tokenMap[0]);
            Assert.Equal((2ul, 2u), tokenMap[1]);
        }


        public IModuleHelpers Helpers => this;

        public ulong Address => 5;

        public bool IsPEFile => true;

        public ulong PEImageBase => 6;

        public ulong ILImageBase => 7;

        public ulong Size => 8;

        public ulong MetadataStart => 9;

        public string Name => "ModuleName";

        public string AssemblyName => "AssemblyName";

        public ulong MetadataLength => 10;

        public bool IsReflection => true;

        public ulong AssemblyAddress => 11;

        public ITypeFactory Factory => null;

        public IDataReader DataReader => null;

        public MetaDataImport GetMetaDataImport(ClrModule module) => throw new NotImplementedException();

        public IReadOnlyList<(ulong, uint)> GetSortedTypeDefMap(ClrModule module) => new (ulong, uint)[] { (1, 1), (2, 2) };

        public IReadOnlyList<(ulong, uint)> GetSortedTypeRefMap(ClrModule module) => new (ulong, uint)[] { (3, 3), (4, 4) };

        public string GetTypeName(ulong mt) => mt.ToString();

        public ClrType TryGetType(ulong mt) => throw new NotImplementedException();
    }
}
