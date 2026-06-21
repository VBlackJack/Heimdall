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

using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Covers the protocol -> credential-field mapping used to inject vault credentials
/// into a profile (<see cref="ServerListViewModel.GetCredentialTarget"/>), focusing on
/// the Telnet and VNC branches added in V-5.
/// </summary>
public sealed class ServerListCredentialTargetTests
{
    // ── Telnet ──────────────────────────────────────────────────────────

    [Fact]
    public void GetCredentialTarget_Telnet_EmptyPassword_TargetsTelnetFields()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "Telnet",
            TelnetPort = 2323,
            TelnetUsername = "",
            TelnetPasswordEncrypted = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        Assert.Equal(2323, target!.Value.Port);
        Assert.Equal("", target.Value.Username);

        target.Value.SetPassword("enc-telnet");
        Assert.Equal("enc-telnet", dto.TelnetPasswordEncrypted);

        target.Value.SetUsernameIfEmpty("vaultuser");
        Assert.Equal("vaultuser", dto.TelnetUsername);
    }

    [Fact]
    public void GetCredentialTarget_Telnet_DoesNotOverwriteExistingUsername()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "Telnet",
            TelnetUsername = "stored",
            TelnetPasswordEncrypted = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        target!.Value.SetUsernameIfEmpty("vaultuser");
        Assert.Equal("stored", dto.TelnetUsername);
    }

    [Fact]
    public void GetCredentialTarget_Telnet_StoredPassword_ReturnsNull()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "Telnet",
            TelnetPasswordEncrypted = "already-set"
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.Null(target);
    }

    // ── VNC ─────────────────────────────────────────────────────────────

    [Fact]
    public void GetCredentialTarget_Vnc_EmptyPassword_TargetsVncPasswordWithNoUsername()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "VNC",
            VncPort = 5901,
            VncPassword = null
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.NotNull(target);
        Assert.Equal(5901, target!.Value.Port);
        Assert.Null(target.Value.Username);

        target.Value.SetPassword("enc-vnc");
        Assert.Equal("enc-vnc", dto.VncPassword);

        // VNC has no username field: the callback is a no-op and must not throw.
        target.Value.SetUsernameIfEmpty("ignored");
    }

    [Fact]
    public void GetCredentialTarget_Vnc_StoredPassword_ReturnsNull()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "VNC",
            VncPassword = "already-set"
        };

        var target = ServerListViewModel.GetCredentialTarget(dto);

        Assert.Null(target);
    }
}
