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
internal sealed class WinRmHandler : IProtocolHandler
{
    private readonly ITunnelService _tunnelService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly WinRmPreflight _preflight;
    private readonly Func<ITerminalSession> _terminalSessionFactory;
    private readonly WinRmPowerShellLaunchBuilder _launchBuilder;
    private readonly Func<WinRmCredentialBootstrap> _credentialBootstrapFactory;
    private readonly WinRmBootstrapJanitor _bootstrapJanitor;

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
        // Best-effort startup cleanup; SweepStaleBootstrapScripts handles its own failures.
        _ = Task.Run(SweepStaleBootstrapScripts);
    }

    public string Protocol => "WINRM";

    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

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

            if (!usesTunnel)
            {
                await _preflight.EnsureReachableAsync(server, ct).ConfigureAwait(false);
            }

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
            ScheduleBootstrapCleanupOnExit(session, bootstrap, bootstrapScriptPath);
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

    private static ITerminalSession CreateDefaultTerminalSession()
    {
        return ConPtySession.IsAvailable
            ? new ConPtySession()
            : new PipeModeSession();
    }

    private void SweepStaleBootstrapScripts()
    {
        try
        {
            _bootstrapJanitor.SweepStale();
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"[WinRmHandler] Startup bootstrap sweep failed: {ex.Message}");
        }
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

    private static void ScheduleBootstrapCleanupOnExit(
        ITerminalSession session,
        WinRmCredentialBootstrap? bootstrap,
        string? bootstrapScriptPath)
    {
        if (bootstrap is null || string.IsNullOrWhiteSpace(bootstrapScriptPath))
        {
            return;
        }

        WinRmCredentialBootstrap capturedBootstrap = bootstrap;
        string capturedPath = bootstrapScriptPath;
        int cleaned = 0;

        void OnProcessExited(int exitCode)
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0)
            {
                return;
            }

            session.ProcessExited -= OnProcessExited;
            capturedBootstrap.Delete(capturedPath);
        }

        session.ProcessExited += OnProcessExited;

        if (!session.IsRunning)
        {
            OnProcessExited(0);
        }
    }
}
