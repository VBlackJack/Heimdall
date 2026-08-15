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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

/// <summary>
/// The shared tool-connection-type surface, and the two predicates it deliberately keeps apart.
/// </summary>
public sealed class ConnectionTypeCatalogToolTests
{
    [Theory]
    [InlineData("TOOL:HASH")]
    [InlineData("tool:hash")]
    [InlineData("Tool:Hash")]
    public void AToolTypeIsRecognisedWhateverItsCase(string connectionType)
    {
        // The application used to answer this question two ways: the four value converters
        // compared ordinally while every logic site ignored case. One comparison now serves both.
        Assert.True(ConnectionTypeCatalog.IsToolConnectionType(connectionType));
    }

    [Fact]
    public void ABarePrefixIsStillAToolTab()
    {
        // Deliberately wider than IsKnown: it names no tool, but it is certainly not a connection
        // either, and the shell has always treated it as a tool tab.
        Assert.True(ConnectionTypeCatalog.IsToolConnectionType("TOOL:"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SSH")]
    [InlineData("TOOLBAR")]
    [InlineData("MYTOOL:HASH")]
    public void EverythingElseIsNotAToolType(string? connectionType)
    {
        // TOOLBAR matters: the prefix is TOOL followed by a colon, so a type that merely starts
        // with those four letters must not be swept up.
        Assert.False(ConnectionTypeCatalog.IsToolConnectionType(connectionType));
    }

    [Theory]
    [InlineData("TOOL:HASH", "HASH")]
    [InlineData("tool:hash", "hash")]
    [InlineData("TOOL:EXT:SCOOP:winbox", "EXT:SCOOP:winbox")]
    public void StrippingReturnsTheIdentifierBehindThePrefix(string connectionType, string expected)
    {
        Assert.Equal(expected, ConnectionTypeCatalog.StripToolPrefix(connectionType));
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("")]
    public void StrippingLeavesANonToolTypeAlone(string connectionType)
    {
        Assert.Equal(connectionType, ConnectionTypeCatalog.StripToolPrefix(connectionType));
    }

    [Fact]
    public void StrippingABarePrefixYieldsNothing()
    {
        Assert.Equal(string.Empty, ConnectionTypeCatalog.StripToolPrefix("TOOL:"));
    }

    [Fact]
    public void WhetherATypeIsKNOWN_StaysStricterThanWhetherItIsAToolTab()
    {
        // The regression this guards. IsKnown and RequiresRemoteServer answer "is this a usable
        // persisted type", which requires an identifier after the prefix. Converging them onto the
        // wider predicate would silently accept a type naming no tool.
        Assert.True(ConnectionTypeCatalog.IsToolConnectionType("TOOL:"));
        Assert.False(ConnectionTypeCatalog.IsKnown("TOOL:"));

        Assert.True(ConnectionTypeCatalog.IsKnown("TOOL:HASH"));
        Assert.True(ConnectionTypeCatalog.IsKnown("SSH"));
    }

    [Fact]
    public void ATypeNamingNoTool_StillRequiresARemoteHost()
    {
        // Same asymmetry seen from the other side: only a named tool is exempt from needing a host.
        Assert.False(ConnectionTypeCatalog.RequiresRemoteServer("TOOL:HASH"));
        Assert.True(ConnectionTypeCatalog.RequiresRemoteServer("TOOL:"));
    }

    [Fact]
    public void ThePrefixIsPubliclyNamed()
    {
        Assert.Equal("TOOL:", ConnectionTypeCatalog.ToolPrefix);
    }
}
