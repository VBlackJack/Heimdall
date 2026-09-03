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

    // The two identifiers this whole mechanism exists to keep apart. They have the same shape -
    // a name, an underscore, eight hexadecimal characters - and nothing in the string says which
    // is which. One was minted for a session of "prod"; the other is an ordinary profile that an
    // import brought in under a name of that shape.
    //
    // Deciding by shape sent both to "prod", which handed an imported profile's certificate
    // approval to an unrelated one. Deciding by looking them up in the inventory sent the second
    // to "prod" as well, as soon as that profile was deleted - which can happen while its own
    // connection is still being established, since deleting a profile does not end it.
    [Fact]
    public void OnlyAnIdentifierThisProcessMintedIsResolvedToAnotherProfile()
    {
        string minted = SessionIdCodec.Create("prod");
        const string imported = "prod_deadbeef";

        Assert.Equal("prod", SessionIdCodec.ResolveInventoryId(minted));
        Assert.Equal(imported, SessionIdCodec.ResolveInventoryId(imported));

        // The control that keeps the pair meaningful: both really do read as a mint by shape, so
        // the difference above is the record and not the text.
        Assert.True(SessionIdCodec.TryGetInventoryId(minted, out string mintedPrefix));
        Assert.True(SessionIdCodec.TryGetInventoryId(imported, out string importedPrefix));
        Assert.Equal("prod", mintedPrefix);
        Assert.Equal("prod", importedPrefix);
    }

    [Fact]
    public void AnIdentifierWithNoMintedShapeAtAllIsItsOwn()
        => Assert.Equal("plain-profile", SessionIdCodec.ResolveInventoryId("plain-profile"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyRuntimeIdentifierIsEchoed(string runtimeId)
        => Assert.Equal(runtimeId, SessionIdCodec.ResolveInventoryId(runtimeId));

}
