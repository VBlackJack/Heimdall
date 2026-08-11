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
using System.Net.Sockets;
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
public sealed class WinRmHandlerGatewayTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Constructor_YoungBootstrapOrphan_ReschedulesAndDeletesAtEligibility()
    {
        DateTime firstSweepUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        TimeSpan maxAge = TimeSpan.FromMinutes(10);
        DateTime lastWriteUtc = firstSweepUtc - TimeSpan.FromMinutes(5);
        DateTime eligibilityUtc = lastWriteUtc + maxAge;
        string orphanPath = Path.Combine(
            Path.GetTempPath(),
            "heimdall_winrm_scheduler_wiring.ps1");
        int sweepCount = 0;
        int utcNowCallCount = 0;
        TaskCompletionSource<string> deleted = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        WinRmBootstrapJanitor janitor = new WinRmBootstrapJanitor(
            enumerateScripts: _ =>
            {
                Interlocked.Increment(ref sweepCount);
                return new string[] { orphanPath };
            },
            getLastWriteTimeUtc: _ => lastWriteUtc,
            delete: path => deleted.TrySetResult(path),
            utcNow: () => Interlocked.Increment(ref utcNowCallCount) == 1
                ? firstSweepUtc
                : eligibilityUtc,
            maxAge: maxAge);

        using WinRmHandler handler = CreateHandler(
            new FakeTunnelService(),
            new CountingWinRmPreflight(),
            new CapturingTerminalSession(),
            bootstrapJanitor: janitor);

        string deletedPath = await deleted.Task.WaitAsync(TestTimeout);

        Assert.Equal(orphanPath, deletedPath);
        Assert.Equal(2, Volatile.Read(ref sweepCount));
        Assert.Equal(2, Volatile.Read(ref utcNowCallCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_ForcedTerminalClose_DeletesBootstrapWithoutProcessExit(bool killFirst)
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"heimdall_winrm_cleanup_{Guid.NewGuid():N}");
        string scriptPath = Path.Combine(testDirectory, "bootstrap.ps1");
        Directory.CreateDirectory(testDirectory);

        try
        {
            WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
                createScriptPath: () => scriptPath,
                writeAndProtect: (path, content) => File.WriteAllText(path, content),
                unprotectStoredPasswordBytes: _ => Encoding.UTF8.GetBytes("secret"),
                protectBootstrapPasswordBytes: _ => "protected-bootstrap-password");
            CapturingTerminalSession terminalSession = new CapturingTerminalSession();
            using WinRmHandler handler = CreateHandler(
                new FakeTunnelService(),
                new CountingWinRmPreflight(),
                terminalSession,
                credentialBootstrapFactory: () => bootstrap);
            ServerProfileDto server = CreateDirectServer();
            server.WinRmIdentityMode = WinRmIdentityMode.Credential;
            server.WinRmUsername = "user";
            server.WinRmPasswordEncrypted = "encrypted";

            ConnectionResult result = await handler.ConnectAsync(
                server,
                new AppSettings(),
                CancellationToken.None);
            TerminalSessionResult terminalResult = Assert.IsType<TerminalSessionResult>(result.Session);
            Assert.True(File.Exists(scriptPath));

            if (killFirst)
            {
                terminalResult.Session.Kill();
                Assert.False(File.Exists(scriptPath));
            }

            terminalResult.Session.Dispose();

            Assert.False(File.Exists(scriptPath));
            Assert.True(terminalSession.IsDisposed);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_ProcessExit_DeletesBootstrapAndForwardsExitEvent()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"heimdall_winrm_exit_{Guid.NewGuid():N}");
        string scriptPath = Path.Combine(testDirectory, "bootstrap.ps1");
        Directory.CreateDirectory(testDirectory);

        try
        {
            WinRmCredentialBootstrap bootstrap = new WinRmCredentialBootstrap(
                createScriptPath: () => scriptPath,
                writeAndProtect: (path, content) => File.WriteAllText(path, content),
                unprotectStoredPasswordBytes: _ => Encoding.UTF8.GetBytes("secret"),
                protectBootstrapPasswordBytes: _ => "protected-bootstrap-password");
            CapturingTerminalSession terminalSession = new CapturingTerminalSession();
            using WinRmHandler handler = CreateHandler(
                new FakeTunnelService(),
                new CountingWinRmPreflight(),
                terminalSession,
                credentialBootstrapFactory: () => bootstrap);
            ServerProfileDto server = CreateDirectServer();
            server.WinRmIdentityMode = WinRmIdentityMode.Credential;
            server.WinRmUsername = "user";
            server.WinRmPasswordEncrypted = "encrypted";

            ConnectionResult result = await handler.ConnectAsync(
                server,
                new AppSettings(),
                CancellationToken.None);
            TerminalSessionResult terminalResult = Assert.IsType<TerminalSessionResult>(result.Session);
            int exitEventCount = 0;
            terminalResult.Session.ProcessExited += _ => exitEventCount++;
            Assert.True(File.Exists(scriptPath));

            terminalSession.RaiseProcessExited(0);

            Assert.False(File.Exists(scriptPath));
            Assert.Equal(1, exitEventCount);
            terminalResult.Session.Dispose();
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_TunneledProfile_LaunchesPowerShellAgainstTunnelEndpoint()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55985
        };
        CountingWinRmPreflight preflight = new CountingWinRmPreflight();
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(tunnelService, preflight, terminalSession);

        ConnectionResult result = await handler.ConnectAsync(
            CreateGatewayServer(),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(terminalSession.Arguments);
        Assert.Contains("-ComputerName '127.0.0.1'", terminalSession.Arguments, StringComparison.Ordinal);
        Assert.Contains("-Port 55985", terminalSession.Arguments, StringComparison.Ordinal);
        Assert.Equal(1, preflight.TcpProbeCount);
        Assert.Equal("127.0.0.1", preflight.LastTcpHost);
        Assert.Equal(55985, preflight.LastTcpPort);
        Assert.Equal("WarnWinRmGatewayKerberos", result.Warning);
    }

    [Fact]
    public async Task ConnectAsync_TunneledCredentialProfile_PreflightFailureStopsBeforeBootstrap()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55985
        };
        CountingWinRmPreflight preflight = new CountingWinRmPreflight
        {
            TcpException = new SocketException((int)SocketError.ConnectionRefused)
        };
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        int bootstrapFactoryCallCount = 0;
        WinRmHandler handler = CreateHandler(
            tunnelService,
            preflight,
            terminalSession,
            () =>
            {
                bootstrapFactoryCallCount++;
                return CreateTestBootstrap();
            });
        ServerProfileDto server = CreateGatewayServer();
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;
        server.WinRmUsername = @"CONTOSO\operator";
        server.WinRmPasswordEncrypted = "encrypted";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, preflight.TcpProbeCount);
        Assert.Equal("127.0.0.1", preflight.LastTcpHost);
        Assert.Equal(55985, preflight.LastTcpPort);
        Assert.Equal(0, bootstrapFactoryCallCount);
        Assert.Null(terminalSession.Arguments);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(55985, tunnelService.ReleasedLocalPort);
    }

    [Fact]
    public async Task ConnectAsync_TunneledProfile_MarksSessionHandedOffInsteadOfConnected()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55985
        };
        ConnectionStateMachine stateMachine = new ConnectionStateMachine();
        Assert.True(stateMachine.TryTransition("winrm-gateway-test", ConnectionState.Initializing));
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(
            tunnelService,
            new CountingWinRmPreflight(),
            terminalSession,
            stateMachine: stateMachine);

        ConnectionResult result = await handler.ConnectAsync(
            CreateGatewayServer(),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.IsType<TerminalSessionResult>(result.Session);
        Assert.Equal(ConnectionState.RemoteSessionHandedOff, stateMachine.GetState("winrm-gateway-test"));
        Assert.NotEqual(ConnectionState.Connected, stateMachine.GetState("winrm-gateway-test"));
    }

    [Fact]
    public async Task ConnectAsync_TunneledHttpsProfile_FailsFastAndReleasesTunnel()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55986
        };
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(
            tunnelService,
            new CountingWinRmPreflight(),
            terminalSession);
        ServerProfileDto server = CreateGatewayServer();
        server.WinRmUseSsl = true;
        server.WinRmPort = DefaultPorts.WinRmHttps;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ErrorWinRmSslGatewayUnsupported", result.ErrorMessage);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(55986, tunnelService.ReleasedLocalPort);
        Assert.Null(terminalSession.Arguments);
    }

    [Fact]
    public async Task ConnectAsync_DirectProfile_RunsPreflight()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = false
        };
        CountingWinRmPreflight preflight = new CountingWinRmPreflight();
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(tunnelService, preflight, terminalSession);

        ConnectionResult result = await handler.ConnectAsync(
            CreateDirectServer(),
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, preflight.TcpProbeCount);
        Assert.Null(result.Warning);
    }

    [Theory]
    [InlineData(" server01.contoso.local ")]
    [InlineData("\r\n*.server01.contoso.local\r\n")]
    public async Task ConnectAsync_DirectProfile_CanonicalizesHostBeforeAllConsumers(string configuredHost)
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = false
        };
        CountingWinRmPreflight preflight = new CountingWinRmPreflight();
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        using WinRmHandler handler = CreateHandler(tunnelService, preflight, terminalSession);
        ServerProfileDto server = CreateDirectServer();
        server.RemoteServer = configuredHost;

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("server01.contoso.local", server.RemoteServer);
        Assert.Equal("server01.contoso.local", preflight.LastTcpHost);
        Assert.Contains(
            "-ComputerName 'server01.contoso.local'",
            terminalSession.Arguments,
            StringComparison.Ordinal);
        TerminalSessionResult session = Assert.IsType<TerminalSessionResult>(result.Session);
        Assert.Equal("server01.contoso.local", session.Endpoint);
    }

    [Fact]
    public async Task ConnectAsync_DirectHttpsProfileWithSkipCertificateCheck_ReturnsWarning()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = false
        };
        CountingWinRmPreflight preflight = new CountingWinRmPreflight();
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(tunnelService, preflight, terminalSession);
        ServerProfileDto server = CreateDirectHttpsServer(skipCertificateCheck: true);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("WarnWinRmSkipCertificateCheck", result.Warning);
        Assert.Equal(1, preflight.TlsProbeCount);
        Assert.True(preflight.LastSkipCertValidation);
    }

    [Fact]
    public async Task ConnectAsync_DirectHttpsProfileWithStrictCertificateCheck_DoesNotReturnSkipWarning()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = false
        };
        CountingWinRmPreflight preflight = new CountingWinRmPreflight();
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(tunnelService, preflight, terminalSession);
        ServerProfileDto server = CreateDirectHttpsServer(skipCertificateCheck: false);

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Warning);
        Assert.Equal(1, preflight.TlsProbeCount);
        Assert.False(preflight.LastSkipCertValidation);
    }

    [Fact]
    public async Task ConnectAsync_TunneledCredentialMode_DoesNotWarnAboutKerberosFallback()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55985
        };
        CapturingTerminalSession terminalSession = new CapturingTerminalSession();
        WinRmHandler handler = CreateHandler(
            tunnelService,
            new CountingWinRmPreflight(),
            terminalSession,
            CreateTestBootstrap);
        ServerProfileDto server = CreateGatewayServer();
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;
        server.WinRmUsername = @"CONTOSO\operator";
        server.WinRmPasswordEncrypted = "encrypted";

        ConnectionResult result = await handler.ConnectAsync(
            server,
            new AppSettings(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Warning);
        Assert.NotNull(terminalSession.Arguments);
        Assert.Contains("-File", terminalSession.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectAsync_PostTunnelLaunchFailure_ReleasesTunnelReference()
    {
        FakeTunnelService tunnelService = new FakeTunnelService
        {
            UsesTunnel = true,
            TargetHost = "127.0.0.1",
            TargetPort = 55985
        };
        CapturingTerminalSession terminalSession = new CapturingTerminalSession
        {
            StartException = new ApplicationException("start failed")
        };
        WinRmHandler handler = CreateHandler(
            tunnelService,
            new CountingWinRmPreflight(),
            terminalSession);

        ConnectionResult result = await handler.ConnectAsync(
            CreateGatewayServer(),
            new AppSettings(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(1, tunnelService.ReleaseCount);
        Assert.Equal(55985, tunnelService.ReleasedLocalPort);
    }

    private static WinRmHandler CreateHandler(
        FakeTunnelService tunnelService,
        CountingWinRmPreflight preflight,
        CapturingTerminalSession terminalSession,
        Func<WinRmCredentialBootstrap>? credentialBootstrapFactory = null,
        ConnectionStateMachine? stateMachine = null,
        WinRmBootstrapJanitor? bootstrapJanitor = null)
    {
        return new WinRmHandler(
            tunnelService,
            stateMachine ?? new ConnectionStateMachine(),
            new LocalizationManager(),
            preflight.Create(),
            () => terminalSession,
            new WinRmPowerShellLaunchBuilder(_ => "powershell.exe"),
            credentialBootstrapFactory,
            bootstrapJanitor ?? CreateNoOpJanitor());
    }

    private static WinRmBootstrapJanitor CreateNoOpJanitor()
    {
        return new WinRmBootstrapJanitor(enumerateScripts: _ => Array.Empty<string>());
    }

    private static WinRmCredentialBootstrap CreateTestBootstrap()
    {
        return new WinRmCredentialBootstrap(
            createScriptPath: () => @"C:\Temp\heimdall_winrm_test.ps1",
            writeAndProtect: (string path, string content) => { },
            unprotectStoredPasswordBytes: encrypted =>
                encrypted == "encrypted" ? Encoding.UTF8.GetBytes("secret") : null,
            protectBootstrapPasswordBytes: _ => "dpapi-bootstrap-blob");
    }

    private static ServerProfileDto CreateGatewayServer()
    {
        return new ServerProfileDto
        {
            Id = "winrm-gateway-test",
            DisplayName = "WinRM Gateway Test",
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttp,
            WinRmUseSsl = false,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser,
            SshGatewayId = "gateway-01",
            UseDirectConnection = false
        };
    }

    private static ServerProfileDto CreateDirectServer()
    {
        return new ServerProfileDto
        {
            Id = "winrm-direct-test",
            DisplayName = "WinRM Direct Test",
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttp,
            WinRmUseSsl = false,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser,
            UseDirectConnection = true
        };
    }

    private static ServerProfileDto CreateDirectHttpsServer(bool skipCertificateCheck)
    {
        ServerProfileDto server = CreateDirectServer();
        server.WinRmPort = DefaultPorts.WinRmHttps;
        server.WinRmUseSsl = true;
        server.WinRmSkipCertificateCheck = skipCertificateCheck;
        return server;
    }

    private sealed class CountingWinRmPreflight
    {
        public int TcpProbeCount { get; private set; }
        public int TlsProbeCount { get; private set; }
        public bool LastSkipCertValidation { get; private set; }
        public string? LastTcpHost { get; private set; }
        public int LastTcpPort { get; private set; }
        public Exception? TcpException { get; init; }

        public WinRmPreflight Create()
        {
            return new WinRmPreflight(
                tcpProbe: ProbeTcpAsync,
                tlsProbe: ProbeTlsAsync);
        }

        private Task ProbeTcpAsync(
            string host,
            int port,
            TimeSpan timeout,
            CancellationToken token)
        {
            TcpProbeCount++;
            LastTcpHost = host;
            LastTcpPort = port;
            if (TcpException is not null)
            {
                return Task.FromException(TcpException);
            }

            return Task.CompletedTask;
        }

        private Task ProbeTlsAsync(
            string host,
            int port,
            TimeSpan timeout,
            bool skipCertValidation,
            CancellationToken token)
        {
            TlsProbeCount++;
            LastSkipCertValidation = skipCertValidation;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTunnelService : ITunnelService
    {
        public bool UsesTunnel { get; init; }
        public string TargetHost { get; init; } = "";
        public int TargetPort { get; init; }
        public int ReleaseCount { get; private set; }
        public int ReleasedLocalPort { get; private set; }

        public Task<(bool Success, bool UsesTunnel, string Host, int Port, string? ErrorMessage)> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            string host = UsesTunnel ? TargetHost : server.RemoteServer;
            int port = UsesTunnel ? TargetPort : remotePort;
            return Task.FromResult((true, UsesTunnel, host, port, (string?)null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public Heimdall.Ssh.TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
            ReleaseCount++;
            ReleasedLocalPort = localPort;
        }
    }

    private sealed class CapturingTerminalSession : ITerminalSession
    {
        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add { }
            remove { }
        }

        public event Action<int>? ProcessExited;

        public bool IsRunning { get; private set; }
        public int? ProcessId => IsRunning ? 1234 : null;
        public Dictionary<string, string>? EnvironmentVariables { get; set; }
        public string? Executable { get; private set; }
        public string? Arguments { get; private set; }
        public string? WorkingDirectory { get; private set; }
        public Exception? StartException { get; init; }
        public bool IsDisposed { get; private set; }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            if (StartException is not null)
            {
                throw StartException;
            }

            Executable = executable;
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            IsRunning = true;
            return Task.CompletedTask;
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

        public void RaiseProcessExited(int exitCode)
        {
            IsRunning = false;
            ProcessExited?.Invoke(exitCode);
        }

        public void Dispose()
        {
            IsDisposed = true;
            IsRunning = false;
        }
    }
}
