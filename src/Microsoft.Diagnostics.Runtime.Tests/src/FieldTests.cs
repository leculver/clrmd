// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class FieldTests
    {
        [Fact]
        public void InstanceFieldProperties()
        {
            using DataTarget dt = TestTargets.Types.LoadFullDump();
            using ClrRuntime runtime = dt.ClrVersions.Single().CreateRuntime();
            ClrHeap heap = runtime.Heap;

            ClrType foo = runtime.GetModule("sharedlibrary.dll").GetTypeByName("Foo");
            Assert.NotNull(foo);

            CheckField(foo, "i", ClrElementType.Int32, "System.Int32", 4);

            CheckField(foo, "s", ClrElementType.String, "System.String", IntPtr.Size);
            CheckField(foo, "b", ClrElementType.Boolean, "System.Boolean", 1);
            CheckField(foo, "f", ClrElementType.Float, "System.Single", 4);
            CheckField(foo, "d", ClrElementType.Double, "System.Double", 8);
            CheckField(foo, "o", ClrElementType.Object, "System.Object", IntPtr.Size);
        }

        private static void CheckField(ClrType type, string fieldName, ClrElementType element, string typeName, int size)
        {
            ClrInstanceField field = type.GetFieldByName(fieldName);
            Assert.NotNull(field);
            Assert.NotNull(field.Type);

            Assert.Equal(element, field.ElementType);
            Assert.Equal(typeName, field.Type.Name);
            Assert.Equal(size, field.Size);
        }

        [Fact]
        public void ReadValueClassTests()
        {
            using DataTarget dt = TestTargets.Types.LoadFullDump();
            using ClrRuntime runtime = dt.ClrVersions.Single().CreateRuntime();
            ClrHeap heap = runtime.Heap;

            ClrObject foo = (from o in heap.EnumerateObjects()
                             where o.Type.Name == "Foo"
                             select o).First();
            Assert.True(foo.IsValidObject);
            
            ClrValueType st = foo.GetValueTypeField("st");

            // Assert that read from a field works and is equal to GetValueTypeField's result
            ClrInstanceField stField = foo.Type.GetFieldByName("st");
            ClrValueType vtFromField = stField.ReadStruct(foo, interior: false);

            Assert.Equal(st, vtFromField);

            // Ensure that the "o" field we read is also enumerated from EnumerateReferencesWithFields and matches
            // our value
            ClrObject obj = st.GetObjectField("o");
            Assert.True(obj.IsValidObject);

            ClrReference objRef = foo.EnumerateReferencesWithFields().Where(r => r.Object == obj).Single();
            Assert.Equal(obj, objRef.Object);
            Assert.Equal(stField, objRef.Field);

            Assert.True(stField.Offset < objRef.Offset);
            Assert.True(objRef.Offset < stField.Offset + objRef.Object.Type.StaticSize);

            // Struct field tests
            ClrValueType struct2 = st.GetValueTypeField("struct2");
            Assert.Equal(13, struct2.GetField<int>("value"));

            // Other field tests
            Assert.Equal("string2", st.GetStringField("s"));
            Assert.Equal(12, st.GetField<int>("j"));
        }
    }
}
