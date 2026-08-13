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

using Heimdall.Core.Import;

namespace Heimdall.Core.Tests.Import;

public sealed class RdpFileParserTests
{
    [Fact]
    public void Parse_EmptyContent_ReturnsEmptySchema()
    {
        var schema = RdpFileParser.Parse(string.Empty);

        Assert.Null(schema.FullAddress);
        Assert.False(schema.HasPasswordBlob);
        Assert.Empty(schema.UnknownKeys);
    }

    [Fact]
    public void Parse_RecognizesCuratedKeys()
    {
        var schema = RdpFileParser.Parse(
            """
            full address:s:rdp.example.com:3390
            username:s:demo
            redirectclipboard:i:1
            redirectprinters:i:0
            redirectsmartcards:i:1
            drivestoredirect:s:*
            use multimon:i:1
            session bpp:i:24
            authentication level:i:2
            enablecredsspsupport:i:1
            gatewayhostname:s:gw.example.com
            gatewayusagemethod:i:1
            """
        );

        Assert.Equal("rdp.example.com:3390", schema.FullAddress);
        Assert.Equal("demo", schema.Username);
        Assert.True(schema.RedirectClipboard);
        Assert.False(schema.RedirectPrinters);
        Assert.True(schema.RedirectSmartCards);
        Assert.Equal("*", schema.DrivesToRedirect);
        Assert.True(schema.UseMultiMon);
        Assert.Equal(24, schema.SessionBpp);
        Assert.Equal(2, schema.AuthenticationLevel);
        Assert.Equal(1, schema.EnableCredSspSupport);
        Assert.Equal("gw.example.com", schema.GatewayHostname);
        Assert.Equal(1, schema.GatewayUsageMethod);
    }

    [Fact]
    public void Parse_ReadsCredSspSupportIndependentlyOfAuthenticationLevel()
    {
        RdpFileSchema schema = RdpFileParser.Parse(
            """
            full address:s:rdp.example.com
            authentication level:i:0
            enablecredsspsupport:i:1
            """
        );

        Assert.Equal(0, schema.AuthenticationLevel);
        Assert.Equal(1, schema.EnableCredSspSupport);
    }

    [Fact]
    public void Parse_LeavesCredSspSupportNull_WhenTheKeyIsAbsent()
    {
        RdpFileSchema schema = RdpFileParser.Parse(
            """
            full address:s:rdp.example.com
            authentication level:i:1
            """
        );

        Assert.Equal(1, schema.AuthenticationLevel);
        Assert.Null(schema.EnableCredSspSupport);
    }

    [Fact]
    public void Parse_IgnoresMalformedLines()
    {
        var schema = RdpFileParser.Parse(
            """
            this is not valid
            another invalid line
            full address:s:demo
            """
        );

        Assert.Equal("demo", schema.FullAddress);
        Assert.Empty(schema.UnknownKeys);
    }

    [Fact]
    public void Parse_TracksUnknownKeys_InLowercase()
    {
        var schema = RdpFileParser.Parse(
            """
            RemoteApplicationName:s:calc.exe
            loadbalanceinfo:s:cookie
            """
        );

        Assert.Equal("calc.exe", schema.UnknownKeys["remoteapplicationname"]);
        Assert.Equal("cookie", schema.UnknownKeys["loadbalanceinfo"]);
    }

    [Fact]
    public void Parse_PasswordBlob_SetsFlagOnly()
    {
        var schema = RdpFileParser.Parse(
            """
            password 51:b:deadbeef
            full address:s:demo
            """
        );

        Assert.True(schema.HasPasswordBlob);
        Assert.DoesNotContain("password 51", schema.UnknownKeys.Keys);
    }

    [Theory]
    [InlineData("redirectclipboard:i:0", false)]
    [InlineData("redirectclipboard:i:1", true)]
    [InlineData("redirectclipboard:i:2", true)]
    public void Parse_BooleanIntegerFields_MapNonZeroToTrue(string line, bool expected)
    {
        var schema = RdpFileParser.Parse(line);

        Assert.Equal(expected, schema.RedirectClipboard);
    }

    [Fact]
    public void Parse_InvalidInt_FallsBackToUnknownKeys()
    {
        var schema = RdpFileParser.Parse("session bpp:i:not-a-number");

        Assert.Null(schema.SessionBpp);
        Assert.Equal("not-a-number", schema.UnknownKeys["session bpp"]);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        var schema = RdpFileParser.Parse(
            """
            Full Address:S:Server01
            Use MultiMon:I:1
            """
        );

        Assert.Equal("Server01", schema.FullAddress);
        Assert.True(schema.UseMultiMon);
    }

    [Fact]
    public void Parse_LastOccurrenceWins()
    {
        var schema = RdpFileParser.Parse(
            """
            username:s:first
            username:s:second
            """
        );

        Assert.Equal("second", schema.Username);
    }

    [Fact]
    public void Parse_StoresDriveRedirectString()
    {
        var schema = RdpFileParser.Parse("drivestoredirect:s:C:,D:");

        Assert.Equal("C:,D:", schema.DrivesToRedirect);
    }

    [Fact]
    public void Parse_StoresScreenModeAndDesktopSize()
    {
        var schema = RdpFileParser.Parse(
            """
            screen mode id:i:2
            desktopwidth:i:1920
            desktopheight:i:1080
            """
        );

        Assert.Equal(2, schema.ScreenModeId);
        Assert.Equal(1920, schema.DesktopWidth);
        Assert.Equal(1080, schema.DesktopHeight);
    }

    [Fact]
    public void Parse_InvalidBooleanType_FallsBackToUnknownKeys()
    {
        var schema = RdpFileParser.Parse("redirectclipboard:s:yes");

        Assert.Null(schema.RedirectClipboard);
        Assert.Equal("yes", schema.UnknownKeys["redirectclipboard"]);
    }

    [Fact]
    public void Parse_SkipsLinesWithEmptyKey()
    {
        var schema = RdpFileParser.Parse(":s:value");

        Assert.Empty(schema.UnknownKeys);
    }

    [Fact]
    public void Parse_UnknownBinaryKey_IsTracked()
    {
        var schema = RdpFileParser.Parse("customblob:b:010203");

        Assert.Equal("010203", schema.UnknownKeys["customblob"]);
    }

    [Fact]
    public void Parse_AlternateFullAddress_IsCaptured()
    {
        var schema = RdpFileParser.Parse("alternate full address:s:alt.example.com");

        Assert.Equal("alt.example.com", schema.AlternateFullAddress);
    }
}
