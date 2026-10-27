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

public sealed class ImportedProfileSanitizerTests
{
    [Fact]
    public void Sanitize_ClearsCitrixLaunchCommandLine_AndPreservesOtherFields()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                Id = "profile-1",
                DisplayName = "Citrix App",
                RemoteServer = "citrix.example.com",
                CitrixId = "store-01@@Calculator",
                CitrixAppName = "Calculator",
                CitrixStoreFrontUrl = "https://citrix.example.com/Citrix/StoreWeb/",
                CitrixLaunchCommandLine = "-launch cached value",
                ExecutionConfirmed = true
            },
            new()
            {
                Id = "profile-2",
                DisplayName = "Standard SSH",
                RemoteServer = "ssh.example.com",
                ConnectionType = "SSH",
                CitrixLaunchCommandLine = null
            }
        };

        ImportedProfileSanitizer.Sanitize(profiles);

        Assert.All(profiles, profile => Assert.Null(profile.CitrixLaunchCommandLine));
        Assert.All(profiles, profile => Assert.False(profile.ExecutionConfirmed));
        Assert.Equal("profile-1", profiles[0].Id);
        Assert.Equal("Citrix App", profiles[0].DisplayName);
        Assert.Equal("citrix.example.com", profiles[0].RemoteServer);
        Assert.Equal("store-01@@Calculator", profiles[0].CitrixId);
        Assert.Equal("Calculator", profiles[0].CitrixAppName);
        Assert.Equal("https://citrix.example.com/Citrix/StoreWeb/", profiles[0].CitrixStoreFrontUrl);
        Assert.Equal("profile-2", profiles[1].Id);
        Assert.Equal("Standard SSH", profiles[1].DisplayName);
        Assert.Equal("ssh.example.com", profiles[1].RemoteServer);
        Assert.Equal("SSH", profiles[1].ConnectionType);
    }

    [Fact]
    public void Sanitize_EmptyList_DoesNotThrow()
    {
        var profiles = new List<ServerProfileDto>();

        var exception = Record.Exception(() => ImportedProfileSanitizer.Sanitize(profiles));

        Assert.Null(exception);
    }

    // Skipping TLS certificate validation is a trust decision about this machine's view of
    // the host, like ExecutionConfirmed: an imported file must not carry it in, with or without
    // SSL. The user re-enables it on the profile if the host really needs it.
    [Fact]
    public void Sanitize_WinRm_ClearsCertificateSkipWithAndWithoutSsl()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                ConnectionType = "WINRM",
                WinRmUseSsl = false,
                WinRmSkipCertificateCheck = true
            },
            new()
            {
                ConnectionType = "WINRM",
                WinRmUseSsl = true,
                WinRmSkipCertificateCheck = true
            }
        };

        ImportedProfileSanitizer.Sanitize(profiles);

        Assert.False(profiles[0].WinRmSkipCertificateCheck);
        Assert.False(profiles[1].WinRmSkipCertificateCheck);
        Assert.True(profiles[1].WinRmUseSsl);
    }

    // S-02. A key or ICA path on a share sends the user's NetNTLMv2 response to the host the
    // file named as soon as anything tests whether it exists. Every local file path field of an
    // imported profile loses such a value, the report keeps the value so the user can re-enter
    // it, and a local value of the same fields stays.
    [Fact]
    public void Sanitize_ClearsNetworkAndDevicePaths_ReportsThemPerProfile_AndKeepsLocalOnes()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                DisplayName = "Remote",
                ConnectionType = "SSH",
                SshKeyPath = @"\\fs\home$\u\.ssh\id_rsa",
                CitrixIcaFilePath = "//attacker.example/share/a.ica",
                LocalShellExecutable = @"\\?\UNC\attacker.example\share\tool.exe",
                LocalShellWorkingDirectory = @"\\attacker.example\share",
                WebExternalProgram = @"\\.\device\browser.exe",
            },
            new()
            {
                DisplayName = "Local",
                ConnectionType = "SSH",
                SshKeyPath = @"C:\Users\me\.ssh\id_ed25519",
                CitrixIcaFilePath = @"C:\Citrix\app.ica",
                LocalShellExecutable = "pwsh.exe",
                LocalShellWorkingDirectory = @"C:\work",
                WebExternalProgram = @"C:\Program Files\Browser\browser.exe",
            },
        };

        ImportedProfileSanitizationReport report = ImportedProfileSanitizer.Sanitize(profiles);

        Assert.Null(profiles[0].SshKeyPath);
        Assert.Null(profiles[0].CitrixIcaFilePath);
        Assert.Null(profiles[0].LocalShellExecutable);
        Assert.Null(profiles[0].LocalShellWorkingDirectory);
        Assert.Null(profiles[0].WebExternalProgram);
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", profiles[1].SshKeyPath);
        Assert.Equal(@"C:\Citrix\app.ica", profiles[1].CitrixIcaFilePath);
        Assert.Equal("pwsh.exe", profiles[1].LocalShellExecutable);
        Assert.Equal(@"C:\work", profiles[1].LocalShellWorkingDirectory);
        Assert.Equal(@"C:\Program Files\Browser\browser.exe", profiles[1].WebExternalProgram);

        ImportedItemSanitization item = Assert.Single(report.Items);
        Assert.Equal("Remote", item.Name);
        Assert.Equal(5, report.ClearedPathCount);
        Assert.Contains(new ImportedPathRemoval("ServerDialogLabelSshKey", @"\\fs\home$\u\.ssh\id_rsa"), item.ClearedPaths);
        Assert.Contains(new ImportedPathRemoval("ServerDialogLabelIcaFilePath", "//attacker.example/share/a.ica"), item.ClearedPaths);
    }

    // S-03. Agent and X11 forwarding and drive and USB redirection hand the remote host something
    // of this machine's. Smart card redirection stays: smart card logon needs it.
    [Fact]
    public void Sanitize_ResetsTheTrustDenylist_KeepsSmartCards_AndReportsPerProfile()
    {
        var profiles = new List<ServerProfileDto>
        {
            new()
            {
                DisplayName = "Shell",
                ConnectionType = "SSH",
                SshAgentForwarding = true,
                SshX11Forwarding = true,
            },
            new()
            {
                DisplayName = "Desktop",
                ConnectionType = "RDP",
                RdpRedirectDrives = true,
                RdpRedirectUsb = true,
                RdpRedirectSmartCards = true,
                RdpRedirectClipboard = true,
            },
            new() { DisplayName = "Plain", ConnectionType = "SSH" },
        };

        ImportedProfileSanitizationReport report = ImportedProfileSanitizer.Sanitize(profiles);

        Assert.False(profiles[0].SshAgentForwarding);
        Assert.False(profiles[0].SshX11Forwarding);
        Assert.False(profiles[1].RdpRedirectDrives);
        Assert.False(profiles[1].RdpRedirectUsb);
        Assert.True(profiles[1].RdpRedirectSmartCards);
        Assert.True(profiles[1].RdpRedirectClipboard);
        Assert.Equal(["Shell", "Desktop"], report.Items.Select(item => item.Name));
        Assert.Equal(
            [nameof(ServerProfileDto.SshAgentForwarding), nameof(ServerProfileDto.SshX11Forwarding)],
            report.Items[0].ResetFlags.Select(flag => flag.PropertyName));
        Assert.Equal(
            [nameof(ServerProfileDto.RdpRedirectDrives), nameof(ServerProfileDto.RdpRedirectUsb)],
            report.Items[1].ResetFlags.Select(flag => flag.PropertyName));
    }

    [Fact]
    public void TrustDenylist_NamesRealBooleanProperties()
    {
        Assert.Equal(4, ImportedTrustFlags.Denylist.Count);
        foreach (ImportedTrustFlag flag in ImportedTrustFlags.Denylist)
        {
            System.Reflection.PropertyInfo? property = typeof(ServerProfileDto).GetProperty(flag.PropertyName);
            Assert.True(property is not null && property.PropertyType == typeof(bool), flag.PropertyName);
        }
    }

    // A replace from a file may not turn a switch on, and leaves one the user had turned on.
    [Fact]
    public void ResetTurnedOn_TurnsOffOnlyWhatTheImportTurnedOn()
    {
        var before = new ServerProfileDto { RdpRedirectDrives = true };
        var updated = new ServerProfileDto { RdpRedirectDrives = true, RdpRedirectUsb = true };

        IReadOnlyList<ImportedTrustFlag> turnedOff = ImportedTrustFlags.ResetTurnedOn(before, updated);

        Assert.True(updated.RdpRedirectDrives);
        Assert.False(updated.RdpRedirectUsb);
        Assert.Equal([nameof(ServerProfileDto.RdpRedirectUsb)], turnedOff.Select(flag => flag.PropertyName));
    }

    [Fact]
    public void Sanitize_NothingToTakeAway_ReportsNothing()
    {
        var profiles = new List<ServerProfileDto>
        {
            new() { ConnectionType = "SSH", SshKeyPath = @"C:\keys\id_rsa", RdpRedirectSmartCards = true },
        };

        Assert.False(ImportedProfileSanitizer.Sanitize(profiles).HasChanges);
    }

    [Fact]
    public void SanitizeGatewayPaths_DropsAShareKeyPath_AndKeepsALocalOne()
    {
        var shared = new SshGatewayDto { Name = "Bastion", KeyPath = @"\\attacker.example\share\id_rsa" };
        var local = new SshGatewayDto { KeyPath = @"C:\keys\bastion" };

        ImportedItemSanitization? item = ImportedProfileSanitizer.SanitizeGatewayPaths(shared);

        Assert.NotNull(item);
        Assert.Equal("Bastion", item.Name);
        Assert.Equal(@"\\attacker.example\share\id_rsa", Assert.Single(item.ClearedPaths).Value);
        Assert.Null(shared.KeyPath);
        Assert.Null(ImportedProfileSanitizer.SanitizeGatewayPaths(local));
        Assert.Equal(@"C:\keys\bastion", local.KeyPath);
    }

    [Fact]
    public void Sanitize_ListContainingNullEntries_DoesNotThrow()
    {
        var profiles = new List<ServerProfileDto>
        {
            null!,
            new()
            {
                Id = "profile-3",
                DisplayName = "Citrix App",
                CitrixLaunchCommandLine = "-launch still here"
            }
        };

        var exception = Record.Exception(() => ImportedProfileSanitizer.Sanitize(profiles));

        Assert.Null(exception);
        Assert.Null(profiles[1].CitrixLaunchCommandLine);
        Assert.Equal("profile-3", profiles[1].Id);
    }
}
