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

using System.Drawing;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Rdp.Display;

namespace Heimdall.App.Tests;

/// <summary>
/// RDP-023. When the requested multi-monitor layout cannot be honoured, the session runs on a copy
/// of the profile carrying coerced display settings. That copy used to be built by a hand-written
/// assignment list which had dropped the session logging override, so falling back silently turned
/// the profile's logging choice back to whatever the global default was.
/// </summary>
/// <remarks>
/// The host monitor count is injected, so the outcome is decided by the test and not by the machine
/// it happens to run on.
/// </remarks>
public sealed class EmbeddedSessionManagerMultimonFallbackTests
{
    private static readonly RdpDisplayCapabilities SingleMonitorHost =
        RdpDisplayCapabilities.FromMonitorBounds([new Rectangle(0, 0, 1920, 1080)]);

    private static readonly RdpDisplayCapabilities DualMonitorHost =
        RdpDisplayCapabilities.FromMonitorBounds(
            [new Rectangle(0, 0, 1920, 1080), new Rectangle(1920, 0, 1920, 1080)]);

    // Three dense monitors left to right, which is the host the finding was reported against.
    private static readonly RdpDisplayCapabilities ThreeDenseMonitorHost =
        RdpDisplayCapabilities.FromMonitorBounds(
        [
            new Rectangle(0, 0, 1920, 1080),
            new Rectangle(1920, 0, 1920, 1080),
            new Rectangle(3840, 0, 1920, 1080),
        ]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AForcedFallback_CarriesTheSessionLoggingOverride(bool logging)
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.SessionLoggingOverride = logging;

        (ServerProfileDto runtime, string? statusKey) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, SingleMonitorHost);

        // The fallback really happened, so the assertion below is not about the pass-through path.
        Assert.NotNull(statusKey);
        Assert.NotSame(server, runtime);

        // false must arrive as false, never as null: an inherited value is a different decision from
        // an explicit refusal to log.
        Assert.Equal(logging, runtime.SessionLoggingOverride);
        Assert.NotNull(runtime.SessionLoggingOverride);
    }

    [Fact]
    public void AForcedFallback_LeavesTheConfiguredProfileUntouched()
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.SessionLoggingOverride = true;

        (ServerProfileDto runtime, _) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, SingleMonitorHost);

        Assert.NotSame(server, runtime);

        // The runtime copy is coerced; the profile the user configured is not.
        Assert.Equal(RdpResolutionMode.Multimon, server.RdpResolutionMode);
        Assert.NotEqual(RdpResolutionMode.Multimon, runtime.RdpResolutionMode);
        Assert.Equal([0, 1], server.RdpSelectedMonitorIndices);
    }

    [Fact]
    public void AForcedFallback_CarriesTheOtherFieldsTheOldListHadDropped()
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.VaultEntryName = "vault-entry";
        server.WinRmUsername = "winrm-user";
        server.WinRmPasswordEncrypted = "winrm-cipher";
        server.WinRmUseSsl = true;
        server.WinRmSkipCertificateCheck = true;
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;
        server.WinRmPort = 5986;

        (ServerProfileDto runtime, _) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, SingleMonitorHost);

        Assert.Equal("vault-entry", runtime.VaultEntryName);
        Assert.Equal("winrm-user", runtime.WinRmUsername);
        Assert.Equal("winrm-cipher", runtime.WinRmPasswordEncrypted);
        Assert.True(runtime.WinRmUseSsl);
        Assert.True(runtime.WinRmSkipCertificateCheck);
        Assert.Equal(WinRmIdentityMode.Credential, runtime.WinRmIdentityMode);
        Assert.Equal(5986, runtime.WinRmPort);
    }

    [Fact]
    public void AForcedFallback_DoesNotFabricateTheKeyPassphrasePresence()
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.SshKeyPath = @"C:\keys\id.ppk";
        server.SshPasswordEncrypted = "cipher";

        Assert.False(server.HasSshKeyPassphraseEncryptedField);
        Assert.True(server.UsesLegacySshCredentialMapping);

        (ServerProfileDto runtime, _) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, SingleMonitorHost);

        Assert.False(runtime.HasSshKeyPassphraseEncryptedField);
        Assert.True(runtime.UsesLegacySshCredentialMapping);
    }

    [Fact]
    public void WithoutAFallback_TheConfiguredProfileIsUsedAsIs()
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.SessionLoggingOverride = true;

        (ServerProfileDto runtime, string? statusKey) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, DualMonitorHost);

        // No copy at all on the common path, so nothing can be dropped by one.
        Assert.Same(server, runtime);
        Assert.Null(statusKey);
    }

    /// <summary>
    /// The finding's own case, end to end: the first and third of three dense monitors.
    /// </summary>
    /// <remarks>
    /// Before this, the selection passed every check, reached the control, and the desktop was
    /// sized from the bounding box, so the session spanned the monitor between them. Nothing was
    /// logged and nothing was shown. The status key matters as much as the coercion: a fallback the
    /// user is not told about is the failure this exists to remove.
    /// </remarks>
    [Fact]
    public void ADisconnectedSelection_IsCoercedAndExplained()
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.RdpSelectedMonitorIndices = [0, 2];

        (ServerProfileDto runtime, string? statusKey) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, ThreeDenseMonitorHost);

        Assert.Equal("RdpMultimonFallbackNonContiguous", statusKey);
        Assert.NotSame(server, runtime);
        Assert.Equal(RdpResolutionMode.FitWindow, runtime.RdpResolutionMode);
        Assert.False(runtime.RdpMultiMonitor);
        Assert.Empty(runtime.RdpSelectedMonitorIndices);

        // The configured profile is a record of what the user asked for and must survive intact.
        Assert.Equal([0, 2], server.RdpSelectedMonitorIndices);
        Assert.Equal(RdpResolutionMode.Multimon, server.RdpResolutionMode);
    }

    /// <summary>
    /// The same host, with a selection that does touch, is left exactly as configured.
    /// </summary>
    [Fact]
    public void AnAdjacentSelection_IsLeftAlone()
    {
        ServerProfileDto server = CreateMultimonProfile();
        server.RdpSelectedMonitorIndices = [1, 2];

        (ServerProfileDto runtime, string? statusKey) =
            EmbeddedSessionManager.ResolveEmbeddedRdpRuntimeServer(server, ThreeDenseMonitorHost);

        Assert.Null(statusKey);
        Assert.Same(server, runtime);
    }

    private static ServerProfileDto CreateMultimonProfile()
    {
        return new ServerProfileDto
        {
            Id = "rdp-023",
            DisplayName = "RDP 023",
            ConnectionType = "RDP",
            RemoteServer = "host.contoso.local",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1],
        };
    }
}
