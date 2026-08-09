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
    private readonly IRdpCredentialManager _credentialManager;
    private readonly Func<string?, string?> _decryptPassword;
    private readonly RdpCredentialAutofillOperation _credentialAutofill;

    public RdpHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        IRdpExternalClientLauncher externalClientLauncher,
        ICredentialGuardService? credentialGuardService = null,
        IRdpCredentialManager? credentialManager = null,
        Func<string?, string?>? decryptPassword = null,
        RdpCredentialAutofillOperation? credentialAutofill = null)
    {
        _tunnelService = tunnelService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _externalClientLauncher = externalClientLauncher;
        _credentialGuardService = credentialGuardService ?? new CredentialGuardService();
        _credentialManager = credentialManager ?? new RdpCredentialManager();
        _decryptPassword = decryptPassword ?? ConnectionHelpers.DecryptPassword;
        _credentialAutofill = credentialAutofill ?? Heimdall.Rdp.CredentialAutofill.WaitAndFillAsync;
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
        string? credentialCleanupTarget = null;
        string? credentialOwnershipMarker = null;
        bool credentialCleanupScheduled = false;
        try
        {
            string rdpHost = targetHost;
            int rdpPort = targetPort;

            if (!string.IsNullOrEmpty(server.RdpUsername) &&
                !string.IsNullOrEmpty(server.RdpPasswordEncrypted))
            {
                rdpPassword = _decryptPassword(server.RdpPasswordEncrypted);
                if (rdpPassword is null)
                {
                    throw new InvalidOperationException(_localizer["RdpErrorDecryptPassword"]);
                }

                string credentialTarget = $"TERMSRV/{rdpHost}";
                string ownershipMarker = _credentialManager.CreateOwnershipMarker();
                if (!_credentialManager.WriteDomainCredential(
                    credentialTarget,
                    server.RdpUsername,
                    rdpPassword,
                    ownershipMarker,
                    out bool credentialWritten,
                    out string? credentialError))
                {
                    Core.Logging.FileLogger.Warn(
                        $"Failed to store RDP credentials: {credentialError ?? "unknown error"}");
                    return new ConnectionResult(
                        false,
                        _localizer["RdpErrorStoreCredentials"],
                        null,
                        RdpSessionDiagnosticFactory.FromCredentialWriteFailure(credentialError));
                }

                if (credentialWritten)
                {
                    credentialCleanupTarget = credentialTarget;
                    credentialOwnershipMarker = ownershipMarker;
                    Core.Logging.FileLogger.Info($"RDP credentials stored for {credentialTarget}");
                }
                else
                {
                    warning = _localizer["RdpExistingWindowsCredentialNotice"];
                    rdpPassword = null;
                    Core.Logging.FileLogger.Info(
                        $"RDP credential injection skipped for {credentialTarget}: existing entry is not owned by Heimdall");
                }
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
                        warning ??= _localizer["WarnRdpFileAclFailed"];
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
                        var filled = await _credentialAutofill(
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

            var cleanupDelay = TimeSpan.FromMilliseconds(settings.RdpArtifactCleanupDelayMs);
            _ = Task.Run(async () =>
            {
                try
                {
                    await CleanupRdpArtifactsAsync(
                            rdpFile,
                            credentialCleanupTarget,
                            credentialOwnershipMarker,
                            cleanupDelay,
                            ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"RDP cleanup failed: {ex.Message}");
                }
            }, CancellationToken.None);
            credentialCleanupScheduled = true;

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
            if (!credentialCleanupScheduled &&
                credentialCleanupTarget is not null &&
                credentialOwnershipMarker is not null)
            {
                ReleaseOwnedCredential(credentialCleanupTarget, credentialOwnershipMarker);
            }

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
    private async Task CleanupRdpArtifactsAsync(
        string rdpFile,
        string? credentialCleanupTarget,
        string? credentialOwnershipMarker,
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

        if (credentialCleanupTarget is not null && credentialOwnershipMarker is not null)
        {
            ReleaseOwnedCredential(credentialCleanupTarget, credentialOwnershipMarker);
        }
    }

    private void ReleaseOwnedCredential(string credentialTarget, string ownershipMarker)
    {
        bool operationSucceeded = _credentialManager.DeleteCredential(
            credentialTarget,
            ownershipMarker,
            out bool credentialDeleted,
            out string? credentialError);
        if (!operationSucceeded)
        {
            Core.Logging.FileLogger.Warn(
                $"RDP CredMan cleanup failed for {credentialTarget}: {credentialError ?? "unknown error"}");
            return;
        }

        if (credentialDeleted)
        {
            Core.Logging.FileLogger.Info($"RDP CredMan entry cleaned: {credentialTarget}");
            return;
        }

        Core.Logging.FileLogger.Info(
            $"RDP CredMan cleanup skipped for {credentialTarget}: ownership marker is absent or changed");
    }
}

internal interface IRdpCredentialManager
{
    string CreateOwnershipMarker();

    bool WriteDomainCredential(
        string targetName,
        string username,
        string password,
        string ownershipMarker,
        out bool credentialWritten,
        out string? error);

    bool DeleteCredential(
        string targetName,
        string ownershipMarker,
        out bool credentialDeleted,
        out string? error);
}

internal delegate Task<bool> RdpCredentialAutofillOperation(
    int processId,
    string host,
    string password,
    TimeSpan timeout,
    CancellationToken cancellationToken);

internal sealed class RdpCredentialManager : IRdpCredentialManager
{
    public string CreateOwnershipMarker()
    {
        return Heimdall.Rdp.CredentialManagerHelper.CreateDomainCredentialOwnershipMarker();
    }

    public bool WriteDomainCredential(
        string targetName,
        string username,
        string password,
        string ownershipMarker,
        out bool credentialWritten,
        out string? error)
    {
        return Heimdall.Rdp.CredentialManagerHelper.WriteDomainCredential(
            targetName,
            username,
            password,
            ownershipMarker,
            out credentialWritten,
            out error);
    }

    public bool DeleteCredential(
        string targetName,
        string ownershipMarker,
        out bool credentialDeleted,
        out string? error)
    {
        return Heimdall.Rdp.CredentialManagerHelper.DeleteCredential(
            targetName,
            ownershipMarker,
            out credentialDeleted,
            out error);
    }
}
