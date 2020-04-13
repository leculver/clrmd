﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.Diagnostics.Runtime.Tests.Fixtures;
using Xunit;

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public class ClrObjectUnitTests : IClassFixture<ClrObjectConnection>
    {
        private readonly ClrObjectConnection _connection;

        private ClrObject _primitiveCarrier => _connection.TestDataClrObject;

        public ClrObjectUnitTests(ClrObjectConnection connection)
            => _connection = connection;

        [Fact]
        public void GetField_WhenBool_ReturnsExpected()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            bool actual = _primitiveCarrier.GetField<bool>(nameof(prototype.TrueBool));

            // Assert
            Assert.True(actual);
        }

        [Fact]
        public void GetField_WhenLong_ReturnsExpected()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            long actual = _primitiveCarrier.GetField<long>(nameof(prototype.OneLargerMaxInt));

            // Assert
            Assert.Equal(prototype.OneLargerMaxInt, actual);
        }

        [Fact]
        public void GetField_WhenEnum_ReturnsExpected()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            ClrObjectConnection.EnumType enumValue = _primitiveCarrier.GetField<ClrObjectConnection.EnumType>(nameof(prototype.SomeEnum));

            // Assert
            Assert.Equal(prototype.SomeEnum, enumValue);
        }

        [Fact]
        public void GetStringField_WhenStringField_ReturnsPointerToObject()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            string text = _primitiveCarrier.GetStringField(nameof(prototype.HelloWorldString));

            // Assert
            Assert.Equal(prototype.HelloWorldString, text);
        }

        [Fact]
        public void GetStringField_WhenTypeMismatch_ThrowsInvalidOperation()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            void readDifferentFieldTypeAsString() => _primitiveCarrier.GetStringField(nameof(prototype.SomeEnum));

            // Assert
            Assert.Throws<InvalidOperationException>(readDifferentFieldTypeAsString);
        }

        [Fact]
        public void GetObjectField_WhenStringField_ReturnsPointerToObject()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            ClrObject textPointer = _primitiveCarrier.GetObjectField(nameof(prototype.HelloWorldString));

            // Assert
            Assert.Equal(prototype.HelloWorldString, (string)textPointer);
        }

        [Fact]
        public void GetObjectField_WhenReferenceField_ReturnsPointerToObject()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            ClrObject referenceFieldValue = _primitiveCarrier.GetObjectField(nameof(prototype.SamplePointer));

            // Assert
            Assert.Equal("SamplePointerType", referenceFieldValue.Type.Name);
        }

        [Fact]
        public void GetObjectField_WhenNonExistingField_ThrowsArgumentException()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            void readNonExistingField() => _primitiveCarrier.GetObjectField("nonExistingField");

            // Assert
            Assert.Throws<ArgumentException>(readNonExistingField);
        }

        [Fact]
        public void GetValueTypeField_WhenDateTime_ThrowsException()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            ClrValueType birthday = _primitiveCarrier.GetValueTypeField(nameof(prototype.Birthday));

            // Assert
            Assert.Equal(typeof(DateTime).FullName, birthday.Type.Name);
        }

        [Fact]
        public void GetValueTypeField_WhenGuid_ThrowsException()
        {
            // Arrange
            var prototype = _connection.Prototype;

            // Act
            ClrValueType sampleGuid = _primitiveCarrier.GetValueTypeField(nameof(prototype.SampleGuid));

            // Assert
            Assert.Equal(typeof(Guid).FullName, sampleGuid.Type.Name);
        }
    }
}
