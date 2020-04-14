﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Linq;

namespace Microsoft.Diagnostics.Runtime
{
    public struct ClrReference
    {
        const ulong OffsetFlag = 0x8000000000000000;
        const ulong DependentFlag = 0x4000000000000000;

        private readonly ulong _offsetOrHandle;

        /// <summary>
        /// The object that <see cref="Field"/> contained.
        /// </summary>
        public ClrObject Object { get; }

        /// <summary>
        /// The offset into the containing object this address is found at.  Only valid if <see cref="IsField"/> is true.
        /// </summary>
        public int Offset
        {
            get
            {
                if ((_offsetOrHandle & OffsetFlag) == OffsetFlag)
                {
                    unchecked
                    {
                        // The (uint) cast will slice off the high bits
                        return (int)(uint)_offsetOrHandle;
                    }
                }

                return -1;
            }
        }

        /// <summary>
        /// The field this object was contained in.  This property may be null if this reference came from
        /// a DependentHandle or if the reference came from an array entry.
        /// Only valid to call if <see cref="IsField"/> is true.
        /// </summary>
        public ClrInstanceField? Field { get; }

        /// <summary>
        /// Returns true if this reference came from a dependent handle.
        /// </summary>
        public bool IsDepenendentHandle => (_offsetOrHandle & DependentFlag) == DependentFlag;

        /// <summary>
        /// Returns true if this reference came from a field in another object.
        /// </summary>
        public bool IsField => (_offsetOrHandle & OffsetFlag) == OffsetFlag && Field != null;

        /// <summary>
        /// Returns true if this reference came from an entry in an array.
        /// </summary>
        public bool IsArrayElement => (_offsetOrHandle & OffsetFlag) == OffsetFlag && Field == null;

        /// <summary>
        /// Create a field reference from a dependent handle value.  We do not keep track of the dependent handle it came from
        /// so we don't accept the value here.
        /// </summary>
        /// <param name="reference">The object referenced.</param>
        public static ClrReference CreateFromDependentHandle(ClrObject reference) => new ClrReference(reference, null, DependentFlag);

        /// <summary>
        /// Creates a ClrFieldReference from an actual field.
        /// </summary>
        /// <param name="reference">The object referenced.</param>
        /// <param name="containingType">The type of the object which points to <paramref name="reference"/>.</param>
        /// <param name="offset">The offset within the source object where <paramref name="reference"/> was located.</param>
        public static ClrReference CreateFromFieldOrArray(ClrObject reference, ClrType containingType, int offset)
        {
            if (containingType == null)
                throw new ArgumentNullException(nameof(containingType));

            offset -= IntPtr.Size;
            DebugOnly.Assert(offset >= 0);

            ClrInstanceField? field = containingType.IsArray ? null : containingType.Fields.First(f => f.Offset <= offset && offset < f.Offset + f.Size);
            unchecked
            {
                return new ClrReference(reference, field, OffsetFlag | (uint)offset);
            }
        }

        private ClrReference(ClrObject obj, ClrInstanceField? field, ulong offsetOrHandleValue)
        {
            _offsetOrHandle = offsetOrHandleValue;
            Object = obj;
            Field = field;
        }
    }
}
