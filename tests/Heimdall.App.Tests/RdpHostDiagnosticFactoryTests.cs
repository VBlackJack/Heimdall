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

using System.IO;
using System.Text.Json;
using Heimdall.App.Views;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class RdpHostDiagnosticFactoryTests
{
    [Theory]
    [InlineData(0, "RdpDisconnectNoInfo")]
    [InlineData(516, "RdpDisconnectSocketConnectFailed")]
    [InlineData(1800, "RdpDisconnectConsoleSessionInProgress")]
    [InlineData(2055, "RdpDisconnectBadCredentials")]
    [InlineData(99999, "RdpDisconnectUnknownCode")]
    public void FromDisconnect_MapsReasonToMessageKey(int code, string expectedKey)
    {
        var diagnostic = RdpHostDiagnosticFactory.FromDisconnect(code);

        Assert.Equal(SessionFailureStage.RdpActiveXDisconnect, diagnostic.Stage);
        Assert.Equal(expectedKey, diagnostic.MessageKey);
        Assert.Equal(code, diagnostic.Code);
        Assert.Null(diagnostic.Detail);
    }

    [Theory]
    [InlineData(2308, 9, "RdpDisconnectBadCredentials")]
    [InlineData(2308, 4, "RdpDisconnectServerLogonTimeout")]
    [InlineData(2308, 257, "RdpDisconnectLicenseError")]
    [InlineData(2308, 266, "RdpDisconnectLicenseError")]
    [InlineData(2308, 267, "RdpDisconnectLicenseError")]
    [InlineData(2308, 768, "RdpDisconnectBadCredentials")]
    [InlineData(516, 0, "RdpDisconnectSocketConnectFailed")]
    [InlineData(1800, 0, "RdpDisconnectConsoleSessionInProgress")]
    public void FromDisconnect_MapsExtendedReasonToMessageKey(
        int code,
        int extendedReason,
        string expectedKey)
    {
        SessionDiagnostic diagnostic = RdpHostDiagnosticFactory.FromDisconnect(code, extendedReason);

        Assert.Equal(SessionFailureStage.RdpActiveXDisconnect, diagnostic.Stage);
        Assert.Equal(expectedKey, diagnostic.MessageKey);
        Assert.Equal(code, diagnostic.Code);
        Assert.Null(diagnostic.Detail);
    }

    [Fact]
    public void FromFatalError_UsesFatalErrorDetailMessageKey()
    {
        var diagnostic = RdpHostDiagnosticFactory.FromFatalError(260);

        Assert.Equal(SessionFailureStage.RdpActiveXDisconnect, diagnostic.Stage);
        Assert.Equal("RdpStatusFatalErrorDetail", diagnostic.MessageKey);
        Assert.Equal(260, diagnostic.Code);
        Assert.Null(diagnostic.Detail);
    }

    [Fact]
    public void FromConnectTimeout_UsesConnectTimeoutDiagnostic()
    {
        SessionDiagnostic diagnostic = RdpHostDiagnosticFactory.FromConnectTimeout();

        Assert.Equal(SessionFailureStage.RdpActiveXConnect, diagnostic.Stage);
        Assert.Equal("RdpDisconnectConnectTimeout", diagnostic.MessageKey);
        Assert.Null(diagnostic.Code);
        Assert.Null(diagnostic.Detail);
    }

    [Fact]
    public void FromTunnelForwardedPortFailure_UsesGatewayTargetDiagnostic()
    {
        var failure = new TunnelForwardedPortFailure(
            54000,
            "host.example",
            3389,
            "Session operation has timed out",
            DateTimeOffset.Parse("2026-05-21T12:00:00Z", null, System.Globalization.DateTimeStyles.AssumeUniversal));

        var diagnostic = RdpHostDiagnosticFactory.FromTunnelForwardedPortFailure(failure, 2308);

        Assert.Equal(SessionFailureStage.RdpTunnel, diagnostic.Stage);
        Assert.Equal("RdpDisconnectGatewayTargetUnreachable", diagnostic.MessageKey);
        Assert.Equal(2308, diagnostic.Code);
        Assert.Equal("host.example:3389", diagnostic.Detail);
    }

    [Theory]
    [InlineData(2308)]
    [InlineData(516)]
    [InlineData(264)]
    [InlineData(772)]
    public void IsTunnelAttributableDisconnect_ReturnsTrue_ForSocketLevelCodes(int reason)
    {
        Assert.True(EmbeddedRdpView.IsTunnelAttributableDisconnect(reason));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(260)]
    [InlineData(2055)]
    public void IsTunnelAttributableDisconnect_ReturnsFalse_ForOtherCodes(int reason)
    {
        Assert.False(EmbeddedRdpView.IsTunnelAttributableDisconnect(reason));
    }

    [Fact]
    public void ResolveDiagnosticFormatArgument_UsesDetailBeforeCode()
    {
        var diagnostic = new SessionDiagnostic(
            SessionFailureStage.RdpTunnel,
            "RdpDisconnectGatewayTargetUnreachable",
            2308,
            "host.example:3389");

        var argument = EmbeddedRdpView.ResolveDiagnosticFormatArgument(diagnostic);

        Assert.Equal("host.example:3389", argument);
    }

    [Fact]
    public void ResolveDiagnosticFormatArgument_FallsBackToCode()
    {
        var diagnostic = new SessionDiagnostic(
            SessionFailureStage.RdpActiveXDisconnect,
            "RdpStatusFatalErrorDetail",
            260,
            null);

        var argument = EmbeddedRdpView.ResolveDiagnosticFormatArgument(diagnostic);

        Assert.Equal(260, argument);
    }

    [Fact]
    public void FatalErrorDetailTemplate_HasCodePlaceholderInBothLocales()
    {
        using var en = LoadLocaleDocument("en");
        Assert.True(en.RootElement.TryGetProperty("RdpStatusFatalErrorDetail", out var enValue));
        Assert.Contains("{0}", enValue.GetString() ?? string.Empty, StringComparison.Ordinal);

        using var fr = LoadLocaleDocument("fr");
        Assert.True(fr.RootElement.TryGetProperty("RdpStatusFatalErrorDetail", out var frValue));
        Assert.Contains("{0}", frValue.GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void FromDisconnect_WithDifferentReasons_UsesDistinctMessageKeys()
    {
        var socketFailure = RdpHostDiagnosticFactory.FromDisconnect(516);
        var resolutionFailure = RdpHostDiagnosticFactory.FromDisconnect(4360);

        Assert.NotEqual(socketFailure.MessageKey, resolutionFailure.MessageKey);
    }

    private static JsonDocument LoadLocaleDocument(string locale)
    {
        var localesPath = FindLocalesPath();
        var jsonPath = Path.Combine(localesPath, $"{locale}.json");

        Assert.True(File.Exists(jsonPath), $"{locale}.json not found at {jsonPath}");

        return JsonDocument.Parse(File.ReadAllText(jsonPath));
    }

    private static string FindLocalesPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "locales");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "en.json")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "locales"));

        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new DirectoryNotFoundException(
            "Cannot find locales/ directory. Ensure the test runs from the repository.");
    }
}
