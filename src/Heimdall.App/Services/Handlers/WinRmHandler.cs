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

using System.Threading;
using System.Threading.Tasks;
using Heimdall.App.Services;
using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;
using Heimdall.Terminal;
using Heimdall.Terminal.ConPty;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles WinRM sessions by launching a local PowerShell host inside an embedded terminal.
/// </summary>
internal sealed class WinRmHandler : IProtocolHandler, IDisposable
{
    private readonly ITunnelService _tunnelService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly WinRmPreflight _preflight;
    private readonly Func<ITerminalSession> _terminalSessionFactory;
    private readonly WinRmPowerShellLaunchBuilder _launchBuilder;
    private readonly Func<WinRmCredentialBootstrap> _credentialBootstrapFactory;
    private readonly WinRmBootstrapJanitor _bootstrapJanitor;
    private readonly SensitiveFileJanitorScheduler _bootstrapJanitorScheduler;

    public WinRmHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        WinRmPreflight? preflight = null,
        Func<ITerminalSession>? terminalSessionFactory = null,
        WinRmPowerShellLaunchBuilder? launchBuilder = null,
        Func<WinRmCredentialBootstrap>? credentialBootstrapFactory = null,
        WinRmBootstrapJanitor? bootstrapJanitor = null)
    {
        _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));
        _connectionSm = connectionSm ?? throw new ArgumentNullException(nameof(connectionSm));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _preflight = preflight ?? new WinRmPreflight();
        _terminalSessionFactory = terminalSessionFactory ?? CreateDefaultTerminalSession;
        _launchBuilder = launchBuilder ?? new WinRmPowerShellLaunchBuilder();
        _credentialBootstrapFactory = credentialBootstrapFactory ?? (() => new WinRmCredentialBootstrap());
        _bootstrapJanitor = bootstrapJanitor ?? new WinRmBootstrapJanitor();
        _bootstrapJanitorScheduler = new SensitiveFileJanitorScheduler(
            nameof(WinRmBootstrapJanitor),
            _bootstrapJanitor.SweepStale);
        _bootstrapJanitorScheduler.Start();
    }

    public string Protocol => "WINRM";

    public void Dispose()
    {
        _bootstrapJanitorScheduler.Dispose();
    }

    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        server.RemoteServer = CanonicalizeRemoteHost(server.RemoteServer);

        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        ITerminalSession? session = null;
        WinRmCredentialBootstrap? bootstrap = null;
        string? bootstrapScriptPath = null;
        bool usesTunnel = false;
        int tunnelLocalPort = 0;

        try
        {
            ct.ThrowIfCancellationRequested();
            WinRmPowerShellLaunchBuilder.ValidateProfile(server);
            int remotePort = WinRmPowerShellLaunchBuilder.ResolvePort(server);

            (bool tunnelOk, usesTunnel, string targetHost, int targetPort, string? tunnelError) =
                await _tunnelService.SetupTunnelIfNeededAsync(server, remotePort, settings, ct)
                    .ConfigureAwait(false);

            if (!tunnelOk)
            {
                string message = tunnelError ?? _localizer["ErrorConnectionFailed"];
                _connectionSm.SetError(server.Id, message);
                return new ConnectionResult(
                    false,
                    message,
                    null,
                    SshSessionDiagnosticFactory.CreateGatewayFailure(tunnelError));
            }

            if (usesTunnel)
            {
                tunnelLocalPort = targetPort;
            }

            if (usesTunnel && server.WinRmUseSsl)
            {
                string message = _localizer["ErrorWinRmSslGatewayUnsupported"];
                ReleaseTunnelIfNeeded(usesTunnel, tunnelLocalPort);
                _connectionSm.SetError(server.Id, message);
                return new ConnectionResult(
                    false,
                    message,
                    null,
                    SshSessionDiagnosticFactory.CreateGatewayFailure(
                        message,
                        "ErrorWinRmSslGatewayUnsupported"));
            }

            if (server.WinRmUseSsl && server.WinRmSkipCertificateCheck)
            {
                Core.Logging.FileLogger.Warn(
                    $"WinRM TLS certificate validation is being skipped for host '{server.RemoteServer}' protocol=WINRM");
            }

            await _preflight.EnsureReachableAsync(server, targetHost, targetPort, ct)
                .ConfigureAwait(false);

            if (server.WinRmIdentityMode == WinRmIdentityMode.Credential)
            {
                bootstrap = _credentialBootstrapFactory();
                bootstrapScriptPath = bootstrap.Write(server, targetHost, targetPort).ScriptPath;
            }

            WinRmPowerShellLaunchSpec spec =
                _launchBuilder.Build(server, targetHost, targetPort, bootstrapScriptPath);

            session = _terminalSessionFactory();

            Core.Logging.FileLogger.Info($"Launching WinRM session for host '{server.RemoteServer}'");
            _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingWinRm);

            await session.StartAsync(
                    spec.Executable,
                    spec.Arguments,
                    workingDirectory: null,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            Core.Logging.FileLogger.Info(
                $"WinRM terminal started for host '{server.RemoteServer}'");

            _connectionSm.TryTransition(server.Id, ConnectionState.RemoteSessionHandedOff);
            session = WrapTerminalSessionWithBootstrapCleanup(session, bootstrap, bootstrapScriptPath);
            string? warning = null;
            if (server.WinRmUseSsl && server.WinRmSkipCertificateCheck)
            {
                warning = _localizer["WarnWinRmSkipCertificateCheck"];
            }
            else if (usesTunnel && server.WinRmIdentityMode == WinRmIdentityMode.CurrentUser)
            {
                warning = _localizer["WarnWinRmGatewayKerberos"];
            }

            return new ConnectionResult(
                true,
                null,
                new TerminalSessionResult(session, server.RemoteServer, server.SessionLoggingOverride),
                Warning: warning);
        }
        catch (OperationCanceledException)
        {
            DeleteBootstrap(bootstrap, bootstrapScriptPath);
            session?.Dispose();
            ReleaseTunnelIfNeeded(usesTunnel, tunnelLocalPort);
            throw;
        }
        catch (WinRmPreflightException ex)
        {
            ReleaseTunnelIfNeeded(usesTunnel, tunnelLocalPort);
            string message = _localizer.Format(ex.LocalizationKey, ex.LocalizationArguments);
            Core.Logging.FileLogger.Warn(
                $"WinRM preflight failed for host '{server.RemoteServer}' protocol=WINRM");
            _connectionSm.SetError(server.Id, message);
            return new ConnectionResult(false, message, null);
        }
        catch (WinRmConfigurationException ex)
        {
            DeleteBootstrap(bootstrap, bootstrapScriptPath);
            session?.Dispose();
            ReleaseTunnelIfNeeded(usesTunnel, tunnelLocalPort);

            string message = _localizer.Format(ex.LocalizationKey, ex.LocalizationArguments);
            Core.Logging.FileLogger.Warn(
                $"WinRM configuration failed for host '{server.RemoteServer}': {ex.Message}");
            _connectionSm.SetError(server.Id, message);
            return new ConnectionResult(false, message, null);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return BuildFailureResult(
                server,
                session,
                bootstrap,
                bootstrapScriptPath,
                usesTunnel,
                tunnelLocalPort,
                "ErrorWinRmInvalidConfiguration",
                ex);
        }
        catch (ArgumentException ex)
        {
            return BuildFailureResult(
                server,
                session,
                bootstrap,
                bootstrapScriptPath,
                usesTunnel,
                tunnelLocalPort,
                "ErrorWinRmInvalidConfiguration",
                ex);
        }
        catch (InvalidOperationException ex)
        {
            return BuildFailureResult(
                server,
                session,
                bootstrap,
                bootstrapScriptPath,
                usesTunnel,
                tunnelLocalPort,
                "ErrorWinRmCredentialUnavailable",
                ex);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error(
                $"WinRM launch failed for host '{server.RemoteServer}': {ex}");
            return BuildFailureResult(
                server,
                session,
                bootstrap,
                bootstrapScriptPath,
                usesTunnel,
                tunnelLocalPort,
                "ErrorWinRmLaunchFailed",
                ex);
        }
    }

    private ConnectionResult BuildFailureResult(
        ServerProfileDto server,
        ITerminalSession? session,
        WinRmCredentialBootstrap? bootstrap,
        string? bootstrapScriptPath,
        bool usesTunnel,
        int tunnelLocalPort,
        string localizationKey,
        Exception exception)
    {
        DeleteBootstrap(bootstrap, bootstrapScriptPath);
        session?.Dispose();

        string message = _localizer[localizationKey];

        Core.Logging.FileLogger.Warn(
            $"WinRM connection failed for host '{server.RemoteServer}': {exception.Message}");
        ReleaseTunnelIfNeeded(usesTunnel, tunnelLocalPort);
        _connectionSm.SetError(server.Id, message);
        return new ConnectionResult(false, message, null);
    }

    private void ReleaseTunnelIfNeeded(bool usesTunnel, int tunnelLocalPort)
    {
        if (!usesTunnel || tunnelLocalPort <= 0)
        {
            return;
        }

        _tunnelService.ReleaseTunnelReference(tunnelLocalPort);
    }

    private static string CanonicalizeRemoteHost(string? remoteHost)
    {
        return string.IsNullOrWhiteSpace(remoteHost)
            ? string.Empty
            : remoteHost.Trim().TrimStart('*', '.');
    }

    private static ITerminalSession CreateDefaultTerminalSession()
    {
        return ConPtySession.IsAvailable
            ? new ConPtySession()
            : new PipeModeSession();
    }

    private static void DeleteBootstrap(
        WinRmCredentialBootstrap? bootstrap,
        string? bootstrapScriptPath)
    {
        if (bootstrap is null || string.IsNullOrWhiteSpace(bootstrapScriptPath))
        {
            return;
        }

        bootstrap.Delete(bootstrapScriptPath);
    }

    private static ITerminalSession WrapTerminalSessionWithBootstrapCleanup(
        ITerminalSession session,
        WinRmCredentialBootstrap? bootstrap,
        string? bootstrapScriptPath)
    {
        if (bootstrap is null || string.IsNullOrWhiteSpace(bootstrapScriptPath))
        {
            return session;
        }

        return new BootstrapCleanupTerminalSession(
            session,
            () => bootstrap.Delete(bootstrapScriptPath));
    }

    private sealed class BootstrapCleanupTerminalSession : ITerminalSession
    {
        private readonly ITerminalSession _inner;
        private Action? _cleanup;
        private int _disposed;

        public BootstrapCleanupTerminalSession(ITerminalSession inner, Action cleanup)
        {
            _inner = inner;
            _cleanup = cleanup;
            _inner.ProcessExited += OnProcessExited;

            if (!_inner.IsRunning)
            {
                Cleanup();
            }
        }

        public event Action<ReadOnlyMemory<byte>>? DataReceived
        {
            add => _inner.DataReceived += value;
            remove => _inner.DataReceived -= value;
        }

        public event Action<int>? ProcessExited
        {
            add => _inner.ProcessExited += value;
            remove => _inner.ProcessExited -= value;
        }

        public bool IsRunning => _inner.IsRunning;

        public int? ProcessId => _inner.ProcessId;

        public Dictionary<string, string>? EnvironmentVariables
        {
            get => _inner.EnvironmentVariables;
            set => _inner.EnvironmentVariables = value;
        }

        public Task StartAsync(
            string executable,
            string arguments,
            int columns = 80,
            int rows = 24,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.StartAsync(
                executable,
                arguments,
                columns,
                rows,
                workingDirectory,
                cancellationToken);
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            _inner.Write(data);
        }

        public void Write(string text)
        {
            _inner.Write(text);
        }

        public void Resize(int columns, int rows)
        {
            _inner.Resize(columns, rows);
        }

        public void Kill()
        {
            try
            {
                _inner.Kill();
            }
            finally
            {
                Cleanup();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _inner.Dispose();
            }
            finally
            {
                _inner.ProcessExited -= OnProcessExited;
                Cleanup();
            }
        }

        private void OnProcessExited(int exitCode)
        {
            _ = exitCode;
            Cleanup();
        }

        private void Cleanup()
        {
            Action? cleanup = Interlocked.Exchange(ref _cleanup, null);
            cleanup?.Invoke();
        }
    }
}
