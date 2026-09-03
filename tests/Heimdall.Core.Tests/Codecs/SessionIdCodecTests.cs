/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using Heimdall.Core.Codecs;

namespace Heimdall.Core.Tests;

public sealed class SessionIdCodecTests
{
    [Fact]
    public void Create_ReturnsInventoryIdWithEightLowercaseHexCharacters()
    {
        string actual = SessionIdCodec.Create("server-1");

        Assert.Matches("^server-1_[0-9a-f]{8}$", actual);
    }

    [Fact]
    public void Create_SameInventoryId_ReturnsDifferentSessionIds()
    {
        string first = SessionIdCodec.Create("server-1");
        string second = SessionIdCodec.Create("server-1");

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("server-1")]
    [InlineData("foo_bar")]
    [InlineData("x_deadbeef")]
    public void TryGetInventoryId_CreatedSessionId_ReturnsOriginalInventoryId(string inventoryId)
    {
        string sessionId = SessionIdCodec.Create(inventoryId);

        bool actual = SessionIdCodec.TryGetInventoryId(sessionId, out string parsedInventoryId);

        Assert.True(actual);
        Assert.Equal(inventoryId, parsedInventoryId);
    }

    [Theory]
    [InlineData("server-1")]
    [InlineData("server-1_a1b2c3")]
    [InlineData("server-1_a1b2c3dX")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("_a1b2c3d4")]
    public void TryGetInventoryId_InvalidSessionId_ReturnsFalseAndEchoesInput(string? sessionId)
    {
        bool actual = SessionIdCodec.TryGetInventoryId(sessionId!, out string inventoryId);

        Assert.False(actual);
        Assert.Equal(sessionId, inventoryId);
    }

    [Fact]
    public void TryGetInventoryId_UppercaseHexSuffix_ReturnsInventoryId()
    {
        bool actual = SessionIdCodec.TryGetInventoryId("server-1_A1B2C3D4", out string inventoryId);

        Assert.True(actual);
        Assert.Equal("server-1", inventoryId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_InvalidInventoryId_ThrowsArgumentException(string? inventoryId)
    {
        Assert.ThrowsAny<ArgumentException>(() => SessionIdCodec.Create(inventoryId!));
    }
}
