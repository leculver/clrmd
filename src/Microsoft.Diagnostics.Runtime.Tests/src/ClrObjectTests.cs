// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class ClrObjectTests
    {
        [Fact]
        public void RuntimeTypeTests()
        {
            using DataTarget dt = TestTargets.Types.LoadFullDump();
            using ClrRuntime runtime = dt.ClrVersions.Single().CreateRuntime();

            ClrObject[] rttObjects = runtime.Heap.EnumerateObjects().Where(o => o.IsRuntimeType).ToArray();
            Assert.NotNull(rttObjects);

            foreach (ClrObject rttObj in rttObjects)
            {
                Assert.False(rttObj.IsArray);
                Assert.False(rttObj.IsException);
                Assert.False(rttObj.IsNull);
                Assert.True(rttObj.IsValidObject);

                Assert.NotNull(rttObj.AsRuntimeType());
            }
        }
    }
}
