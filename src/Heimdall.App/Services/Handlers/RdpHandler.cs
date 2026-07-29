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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.StateMachine;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Handles RDP connection logic.
/// </summary>
internal sealed class RdpHandler : IProtocolHandler
{
    private readonly ITunnelService _tunnelService;
    private readonly ConnectionStateMachine _connectionSm;
    private readonly LocalizationManager _localizer;
    private readonly IRdpExternalClientLauncher _externalClientLauncher;
    private readonly ICredentialGuardService _credentialGuardService;

    public RdpHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        IRdpExternalClientLauncher externalClientLauncher,
        ICredentialGuardService? credentialGuardService = null)
    {
        _tunnelService = tunnelService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _externalClientLauncher = externalClientLauncher;
        _credentialGuardService = credentialGuardService ?? new CredentialGuardService();
    }

    public string Protocol => "RDP";

    /// <summary>
    /// Establishes an RDP connection, optionally through an SSH tunnel.
    /// Returns a result containing the tunnel local port (for embedded RDP)
    /// or null on failure.
    /// </summary>
    public async Task<ConnectionResult> ConnectAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct,
        RdpModeOverride rdpModeOverride = RdpModeOverride.UseProfile)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(settings);

        Core.Logging.FileLogger.Info(
            $"ConnectRdpAsync: {server.DisplayName} ({server.RemoteServer}:{server.RemotePort}) Gateway={server.SshGatewayId ?? "none"}");
        _connectionSm.TryTransition(server.Id, ConnectionState.ValidatingConfig);

        var rdpMode = ResolveEffectiveMode(server, rdpModeOverride);
        var isEmbedded = string.Equals(rdpMode, "Embedded", StringComparison.OrdinalIgnoreCase);
        Core.Logging.FileLogger.Info($"RDP mode: {rdpMode}");

        if (settings.RequireCredentialGuard && isEmbedded)
        {
            CredentialGuardStatus credentialGuard =
                await _credentialGuardService.GetStatusAsync(ct).ConfigureAwait(false);
            if (credentialGuard.State is not CredentialGuardState.Active)
            {
                if (credentialGuard.State is CredentialGuardState.Indeterminate)
                {
                    Core.Logging.FileLogger.Warn(
                        _localizer.Format(
                            "LogCredentialGuardCheckFailed",
                            credentialGuard.FailureReason ?? "unknown error"));
                }

                Core.Logging.FileLogger.Warn(
                    _localizer.Format("LogEmbeddedCredentialGuardBlocked", server.DisplayName));
                return new ConnectionResult(
                    false,
                    _localizer["ErrorEmbeddedCredentialGuardRequired"],
                    null);
            }
        }

        var (tunnelOk, usesTunnel, targetHost, targetPort, tunnelError) =
            await _tunnelService.SetupTunnelIfNeededAsync(
                    server,
                    server.RemotePort,
                    settings,
                    ct,
                    preferDistinctLoopback: !isEmbedded)
                .ConfigureAwait(false);

        if (!tunnelOk)
        {
            return new ConnectionResult(
                false,
                tunnelError,
                null,
                RdpSessionDiagnosticFactory.CreateTunnelFailure(tunnelError));
        }

        _connectionSm.TryTransition(server.Id, ConnectionState.LaunchingRdp);

        if (isEmbedded)
        {
            int? effectiveTunnelPort = usesTunnel ? targetPort : null;
            return new ConnectionResult(true, null, new RdpSessionResult(server, effectiveTunnelPort));
        }

        string? rdpPassword = null;
        bool releaseTunnel = usesTunnel;
        string? warning = null;
        try
        {
            string rdpHost = targetHost;
            int rdpPort = targetPort;

            if (!string.IsNullOrEmpty(server.RdpUsername) &&
                !string.IsNullOrEmpty(server.RdpPasswordEncrypted))
            {
                rdpPassword = ConnectionHelpers.DecryptPassword(server.RdpPasswordEncrypted);
                if (rdpPassword is null)
                {
                    throw new InvalidOperationException(_localizer["RdpErrorDecryptPassword"]);
                }

                var credTarget = $"TERMSRV/{rdpHost}";
                if (!Heimdall.Rdp.CredentialManagerHelper.WriteDomainCredential(
                    credTarget,
                    server.RdpUsername,
                    rdpPassword,
                    out var credError))
                {
                    Core.Logging.FileLogger.Warn(
                        $"Failed to store RDP credentials: {credError ?? "unknown error"}");
                    return new ConnectionResult(
                        false,
                        _localizer["RdpErrorStoreCredentials"],
                        null,
                        RdpSessionDiagnosticFactory.FromCredentialWriteFailure(credError));
                }

                Core.Logging.FileLogger.Info($"RDP credentials stored for {credTarget}");
            }

            var rdpFile = Path.Combine(Path.GetTempPath(), $"heimdall_{server.Id}_{Guid.NewGuid():N}.rdp");
            var resolution = RdpProfileResolver.ResolveResolution(server, settings);
            var redirections = RdpProfileResolver.BuildRedirections(server, settings);
            if (resolution.EmitDisabledMultiMonitor)
            {
                redirections.MultiMonitor = false;
            }

            var rdpContent = Heimdall.Rdp.RdpFileGenerator.Generate(new Heimdall.Rdp.RdpFileOptions
            {
                Host = rdpHost,
                Port = rdpPort,
                Username = server.RdpUsername,
                Domain = string.IsNullOrWhiteSpace(server.RdpDomain) ? null : server.RdpDomain,
                Width = resolution.Width,
                Height = resolution.Height,
                ColorDepth = RdpProfileResolver.ResolveColorDepth(server, settings),
                FullScreen = server.RdpFullScreen,
                ScreenMode = resolution.ScreenMode,
                MultiMonitor = resolution.MultiMonitor,
                EmitDisabledMultiMonitor = resolution.EmitDisabledMultiMonitor,
                SmartSizing = resolution.SmartSizing,
                SelectedMonitorIndices = resolution.SelectedMonitorIndices,
                AdminMode = server.RdpAdminMode,
                GatewayHostname = server.RdpGateway,
                Redirections = redirections
            });

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Core.Security.SecureFileWriter.WriteAndProtect(rdpFile, rdpContent);
                }
                catch (Exception swEx)
                {
                    Core.Logging.FileLogger.Error(
                        $"Atomic ACL write failed for .rdp file, falling back to unprotected write: {swEx.Message}");
                    try
                    {
                        await File.WriteAllTextAsync(rdpFile, rdpContent, ct).ConfigureAwait(false);
                    }
                    catch (Exception writeEx)
                    {
                        Core.Logging.FileLogger.Error("Failed to write .rdp file", writeEx);
                        return new ConnectionResult(
                            false,
                            _localizer["RdpErrorRdpFileWrite"],
                            null,
                            RdpSessionDiagnosticFactory.FromRdpFileWriteException(writeEx));
                    }

                    try
                    {
                        Heimdall.Core.Security.AclEnforcer.SetFileAcl(rdpFile);
                    }
                    catch (Exception aclEx)
                    {
                        Core.Logging.FileLogger.Error(
                            $"Failed to set ACL on .rdp file — file has inherited permissions: {aclEx.Message}");
                        warning = _localizer["WarnRdpFileAclFailed"];
                    }
                }
            }
            else
            {
                try
                {
                    await File.WriteAllTextAsync(rdpFile, rdpContent, ct).ConfigureAwait(false);
                }
                catch (Exception writeEx)
                {
                    Core.Logging.FileLogger.Error("Failed to write .rdp file", writeEx);
                    return new ConnectionResult(
                        false,
                        _localizer["RdpErrorRdpFileWrite"],
                        null,
                        RdpSessionDiagnosticFactory.FromRdpFileWriteException(writeEx));
                }
            }

            ILaunchedRdpClientProcess? mstscProcess;
            try
            {
                mstscProcess = _externalClientLauncher.Launch(rdpFile);
            }
            catch (Exception launchEx)
            {
                Core.Logging.FileLogger.Error("RDP launch failed", launchEx);
                return new ConnectionResult(
                    false,
                    _localizer["RdpErrorMstscLaunch"],
                    null,
                    RdpSessionDiagnosticFactory.FromMstscLaunchException(launchEx));
            }

            if (mstscProcess is null)
            {
                var launchEx = new InvalidOperationException(_localizer["RdpErrorMstscLaunch"]);
                Core.Logging.FileLogger.Error("RDP launch failed", launchEx);
                return new ConnectionResult(
                    false,
                    launchEx.Message,
                    null,
                    RdpSessionDiagnosticFactory.FromMstscLaunchException(launchEx));
            }

            var mstscPid = mstscProcess.Id;
            Core.Logging.FileLogger.Info(
                $"Launched mstsc.exe PID={mstscPid} for {server.DisplayName} ({rdpHost}:{rdpPort})");

            var serverIdForExitClosure = server.Id;
            var displayNameForExitClosure = server.DisplayName;
            var stateMachineForExitClosure = _connectionSm;

            mstscProcess.Exited += (_, _) =>
            {
                var exitCode = -1;
                try
                {
                    exitCode = mstscProcess.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    // The process may already be cleaned up by the OS.
                }

                Core.Logging.FileLogger.Info(
                    $"External mstsc.exe exited PID={mstscPid} ExitCode={exitCode} server={displayNameForExitClosure}");

                try
                {
                    stateMachineForExitClosure.TryTransition(
                        serverIdForExitClosure,
                        ConnectionState.Disconnected);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"State transition on mstsc.exe exit failed: {ex.Message}");
                }

                try
                {
                    ReleaseTunnelIfNeeded(usesTunnel, targetPort);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"Tunnel release on mstsc.exe exit failed: {ex.Message}");
                }

                try
                {
                    mstscProcess.Dispose();
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"mstsc.exe Process.Dispose failed: {ex.Message}");
                }
            };
            releaseTunnel = false;
            mstscProcess.EnableRaisingEvents = true;
            _connectionSm.TryTransition(server.Id, ConnectionState.LaunchedExternalClient);

            if (!string.IsNullOrEmpty(rdpPassword) && mstscPid > 0)
            {
                // CredentialAutofill zeroes its char[] buffer after use, but this
                // closure string and its transient UI strings remain heap-resident
                // until GC; that is inherent to .NET immutable strings.
                string autofillPassword = rdpPassword;
                // Autofill must outlive ConnectAsync: the connect-scoped token is cancelled
                // when ConnectAsync returns. WaitAndFillAsync self-bounds via its timeout.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var autofillTimeout = TimeSpan.FromMilliseconds(settings.RdpCredentialAutofillTimeoutMs);
                        var filled = await Heimdall.Rdp.CredentialAutofill.WaitAndFillAsync(
                                mstscPid,
                                rdpHost,
                                autofillPassword,
                                autofillTimeout,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (!filled)
                        {
                            Core.Logging.FileLogger.Warn(
                                $"External RDP CredUI autofill timed out for {server.DisplayName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Core.Logging.FileLogger.Warn($"External RDP CredUI autofill failed: {ex.Message}");
                    }
                }, CancellationToken.None);
            }

            var credCleanupTarget = !string.IsNullOrEmpty(server.RdpUsername) &&
                                    !string.IsNullOrEmpty(server.RdpPasswordEncrypted)
                ? $"TERMSRV/{rdpHost}"
                : null;
            var cleanupDelay = TimeSpan.FromMilliseconds(settings.RdpArtifactCleanupDelayMs);
            _ = Task.Run(async () =>
            {
                try
                {
                    await CleanupRdpArtifactsAsync(rdpFile, credCleanupTarget, cleanupDelay, ct);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"RDP cleanup failed: {ex.Message}");
                }
            }, CancellationToken.None);

            return new ConnectionResult(true, null, null, Warning: warning);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("RDP launch failed", ex);
            return new ConnectionResult(
                false,
                _localizer["RdpErrorLaunchFailed"],
                null,
                RdpSessionDiagnosticFactory.FromGenericException(ex));
        }
        finally
        {
            rdpPassword = null;
            ReleaseTunnelIfNeeded(releaseTunnel, targetPort);
        }
    }

    private void ReleaseTunnelIfNeeded(bool usesTunnel, int tunnelLocalPort)
    {
        if (!usesTunnel || tunnelLocalPort <= 0)
        {
            return;
        }

        _tunnelService.ReleaseTunnelReference(tunnelLocalPort);
    }

    /// <summary>
    /// Resolves the RDP mode for this launch without mutating the profile.
    /// </summary>
    internal static string ResolveEffectiveMode(
        ServerProfileDto server,
        RdpModeOverride rdpModeOverride)
    {
        ArgumentNullException.ThrowIfNull(server);

        return rdpModeOverride switch
        {
            RdpModeOverride.ForceEmbedded => "Embedded",
            RdpModeOverride.ForceExternal => "External",
            _ => server.RdpMode ?? "Embedded"
        };
    }

    /// <summary>
    /// Cleans up the temporary .rdp file and CredMan entry after a delay.
    /// </summary>
    private static async Task CleanupRdpArtifactsAsync(
        string rdpFile,
        string? credCleanupTarget,
        TimeSpan cleanupDelay,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(cleanupDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            File.Delete(rdpFile);
        }
        catch (IOException ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RDP artifact cleanup: failed to delete temp .rdp file '{rdpFile}': {ex.Message}");
        }

        if (credCleanupTarget is not null)
        {
            Heimdall.Rdp.CredentialManagerHelper.DeleteCredential(credCleanupTarget, out _);
            Core.Logging.FileLogger.Info($"RDP CredMan entry cleaned: {credCleanupTarget}");
        }
    }
}
