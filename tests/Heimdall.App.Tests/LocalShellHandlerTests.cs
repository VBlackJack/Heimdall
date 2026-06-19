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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Tests;

public sealed class LocalShellHandlerTests
{
    [Fact]
    public void Protocol_ReturnsLocal()
    {
        LocalShellHandler handler = CreateHandler();

        Assert.Equal("LOCAL", handler.Protocol);
    }

    [Fact]
    public async Task ConnectAsync_NullServer_Throws()
    {
        LocalShellHandler handler = CreateHandler();
        AppSettings settings = new AppSettings();

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            handler.ConnectAsync(null!, settings, CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_WhenShellLaunchFails_ReturnsLocalizedGenericMessage()
    {
        LocalizationManager localizer = await CreateLocalizerAsync("en");
        ConnectionStateMachine stateMachine = new ConnectionStateMachine();
        LocalShellHandler handler = new LocalShellHandler(stateMachine, localizer);
        string missingExecutable = Path.Combine(
            Path.GetTempPath(),
            $"heimdall-missing-shell-{Guid.NewGuid():N}.exe");
        ServerProfileDto server = new ServerProfileDto
        {
            Id = "local-shell-missing-executable",
            ConnectionType = "LOCAL",
            LocalShellExecutable = missingExecutable
        };

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(localizer["ErrorLocalShellLaunchFailed"], result.ErrorMessage);
        Assert.Equal(localizer["ErrorLocalShellLaunchFailed"], stateMachine.GetStateData(server.Id)?.ErrorMessage);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task LocalShellErrorKeys_AreLocalized(string locale)
    {
        LocalizationManager localizer = await CreateLocalizerAsync(locale);
        string[] keys =
        [
            "ErrorLocalShellGsudoNotFound",
            "ErrorLocalShellElevatedStartFailed",
            "ErrorLocalShellElevationCancelled",
            "ErrorLocalShellLaunchFailed"
        ];

        foreach (string key in keys)
        {
            string value = localizer[key];

            Assert.NotEqual(key, value);
            Assert.False(string.IsNullOrWhiteSpace(value));
        }
    }

    [Theory]
    [InlineData("powershell.exe", true)]
    [InlineData("pwsh.exe", true)]
    [InlineData("PowerShell.EXE", true)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData("pwsh", true)]
    [InlineData("cmd.exe", false)]
    [InlineData("bash", false)]
    public void IsPowerShellExecutable_DetectsPowerShellExecutables(string executable, bool expected)
    {
        bool result = LocalShellHandler.IsPowerShellExecutable(executable);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildContextEnvironment_AllFieldsEmpty_ReturnsNull()
    {
        ServerProfileDto server = new ServerProfileDto
        {
            ConnectionType = string.Empty,
            RemotePort = 0
        };

        Dictionary<string, string>? result = LocalShellHandler.BuildContextEnvironment(server);

        Assert.Null(result);
    }

    [Fact]
    public void BuildContextEnvironment_MapsAllFields()
    {
        ServerProfileDto server = new ServerProfileDto
        {
            RemoteServer = "host.example.com",
            DisplayName = "Local Shell",
            RemotePort = 22,
            SshUsername = "ssh-user",
            ConnectionType = "SSH",
            Group = "Production",
            Environment = "prod"
        };

        Dictionary<string, string>? result = LocalShellHandler.BuildContextEnvironment(server);

        Assert.NotNull(result);
        Assert.Equal("host.example.com", result["HEIMDALL_HOST"]);
        Assert.Equal("Local Shell", result["HEIMDALL_NAME"]);
        Assert.Equal("22", result["HEIMDALL_PORT"]);
        Assert.Equal("ssh-user", result["HEIMDALL_USER"]);
        Assert.Equal("SSH", result["HEIMDALL_TYPE"]);
        Assert.Equal("Production", result["HEIMDALL_GROUP"]);
        Assert.Equal("prod", result["HEIMDALL_ENV"]);
    }

    [Fact]
    public void BuildContextEnvironment_SshUsernameWins()
    {
        ServerProfileDto server = new ServerProfileDto
        {
            SshUsername = "ssh-user",
            RdpUsername = "rdp-user"
        };

        Dictionary<string, string>? result = LocalShellHandler.BuildContextEnvironment(server);

        Assert.NotNull(result);
        Assert.Equal("ssh-user", result["HEIMDALL_USER"]);
    }

    [Fact]
    public void BuildContextEnvironment_FallsBackToRdpUsername()
    {
        ServerProfileDto server = new ServerProfileDto
        {
            SshUsername = null,
            RdpUsername = "rdp-user"
        };

        Dictionary<string, string>? result = LocalShellHandler.BuildContextEnvironment(server);

        Assert.NotNull(result);
        Assert.Equal("rdp-user", result["HEIMDALL_USER"]);
    }

    [Fact]
    public void BuildContextEnvironment_PortZeroOmitsPortKey()
    {
        ServerProfileDto server = new ServerProfileDto
        {
            ConnectionType = "LOCAL",
            RemotePort = 0
        };

        Dictionary<string, string>? result = LocalShellHandler.BuildContextEnvironment(server);

        Assert.NotNull(result);
        Assert.False(result.ContainsKey("HEIMDALL_PORT"));
    }

    private static LocalShellHandler CreateHandler()
    {
        return new LocalShellHandler(new ConnectionStateMachine(), new LocalizationManager());
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        LocalizationManager manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }
}
