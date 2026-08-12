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
using System.Text;
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Terminal;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class WinRmHandlerBootstrapCleanupTests
{
    [Fact]
    public async Task ConnectAsync_CredentialMode_WhenProcessExits_DeletesBootstrapScript()
    {
        string scriptPath = CreateScriptPath("process_exit");

        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService
            {
                UsesTunnel = true,
                TargetHost = "127.0.0.1",
                TargetPort = 55985
            };
            RaisingTerminalSession terminalSession = new RaisingTerminalSession();
            WinRmHandler handler = CreateHandler(
                tunnelService,
                terminalSession,
                () => CreateTestBootstrap(scriptPath));

            ConnectionResult result = await handler.ConnectAsync(
                CreateCredentialServer(),
                new AppSettings(),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.True(File.Exists(scriptPath));

            terminalSession.RaiseProcessExited(0);

            Assert.False(File.Exists(scriptPath));
        }
        finally
        {
            DeleteIfExists(scriptPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_CredentialMode_WhenProcessAlreadyExited_DeletesBootstrapScriptImmediately()
    {
        string scriptPath = CreateScriptPath("already_exited");

        try
        {
            FakeTunnelService tunnelService = new FakeTunnelService
            {
                UsesTunnel = true,
                TargetHost = "127.0.0.1",
                TargetPort = 55985
            };
            RaisingTerminalSession terminalSession = new RaisingTerminalSession
            {
                IsRunningAfterStart = false
            };
            WinRmHandler handler = CreateHandler(
                tunnelService,
                terminalSession,
                () => CreateTestBootstrap(scriptPath));

            ConnectionResult result = await handler.ConnectAsync(
                CreateCredentialServer(),
                new AppSettings(),
                CancellationToken.None);

            Assert.True(result.Success);
            Assert.False(File.Exists(scriptPath));
        }
        finally
        {
            DeleteIfExists(scriptPath);
        }
    }

    [Fact]
    public async Task ConnectAsync_CurrentUserMode_WhenProcessExits_DoesNotThrow()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55985
        };
        RaisingTerminalSession terminalSession = new RaisingTerminalSession();
        WinRmHandler handler = CreateHandler(
            tunnelService,
            terminalSession,
            () => throw new InvalidOperationException("CurrentUser mode should not create bootstrap."));

        ConnectionResult result = await handler.ConnectAsync(
            CreateCurrentUserServer(),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Exception? exception = Record.Exception(() => terminalSession.RaiseProcessExited(0));
        Assert.Null(exception);
    }

    private static WinRmHandler CreateHandler(
        FakeTunnelService tunnelService,
        RaisingTerminalSession terminalSession,
        Func<WinRmCredentialBootstrap> credentialBootstrapFactory)
    {
        return new WinRmHandler(
            tunnelService,
            new ConnectionStateMachine(),
            new LocalizationManager(),
            new CountingWinRmPreflight().Create(),
            () => terminalSession,
            new WinRmPowerShellLaunchBuilder(_ => "powershell.exe"),
            credentialBootstrapFactory,
            CreateNoOpJanitor());
    }

    private static WinRmBootstrapJanitor CreateNoOpJanitor()
    {
        return new WinRmBootstrapJanitor(enumerateScripts: _ => Array.Empty<string>());
    }

    private static WinRmCredentialBootstrap CreateTestBootstrap(string scriptPath)
    {
        return new WinRmCredentialBootstrap(
            createScriptPath: () => scriptPath,
            writeAndProtect: (string path, string content) => File.WriteAllText(path, content),
            unprotectStoredPasswordBytes: encrypted =>
                encrypted == "encrypted" ? Encoding.UTF8.GetBytes("secret") : null,
            protectBootstrapPasswordBytes: _ => "dpapi-bootstrap-blob");
    }

    private static string CreateScriptPath(string scenario)
    {
        return Path.Combine(
            Path.GetTempPath(),
            $"heimdall_winrm_{scenario}_{Guid.NewGuid():N}.ps1");
    }

    private static void DeleteIfExists(string scriptPath)
    {
        if (File.Exists(scriptPath))
        {
            File.Delete(scriptPath);
        }
    }

    private static ServerProfileDto CreateCredentialServer()
    {
        ServerProfileDto server = CreateCurrentUserServer();
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;
        server.WinRmUsername = @"CONTOSO\operator";
        server.WinRmPasswordEncrypted = "encrypted";
        return server;
    }

    private static ServerProfileDto CreateCurrentUserServer()
    {
        return new ServerProfileDto
        {
            Id = "winrm-bootstrap-cleanup-test",
            DisplayName = "WinRM Bootstrap Cleanup Test",
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttp,
            WinRmUseSsl = false,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser,
            SshGatewayId = "gateway-01",
            UseDirectConnection = false
        };
    }

    private sealed class CountingWinRmPreflight
    {
        public WinRmPreflight Create()
        {
            return new WinRmPreflight(tcpProbe: ProbeTcpAsync);
        }

        private static Task ProbeTcpAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken token)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public bool UsesTunnel { get; init; }
        public string TargetHost { get; init; } = "";
        public int TargetPort { get; init; }

        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            string host = UsesTunnel ? TargetHost : server.RemoteServer;
            int port = UsesTunnel ? TargetPort : remotePort;
            return Task.FromResult(new TunnelSetupOutcome(true, UsesTunnel, host, port, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class RaisingTerminalSession : ITerminalSession
    {
        private Action<int>? _processExited;

        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? ProcessExited
        {
            add => _processExited += value;
            remove => _processExited -= value;
        }

        public bool IsRunning { get; private set; }
        public bool IsRunningAfterStart { get; init; } = true;
        public int? ProcessId => IsRunning ? 1234 : null;
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
        public string? Executable { get; private set; }
        public string? Arguments { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public bool IsDisposed { get; private set; }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            Executable = executable;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            IsRunning = IsRunningAfterStart;
            return Task.CompletedTask;
        }

        public void RaiseProcessExited(int exitCode)
        {
            IsRunning = false;
            _processExited?.Invoke(exitCode);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
        }

        public void Write(string text)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        public void Kill()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            IsDisposed = true;
            IsRunning = false;
        }
    }
}
