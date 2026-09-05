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
    private readonly Action<string> _deleteRdpFile;
    private readonly Func<TimeSpan, Task> _artifactCleanupDelay;
    private readonly Action _sweepStaleRdpArtifacts;

    /// <summary>
    /// The deferred cleanups that have not run yet, keyed by artifact path. Consulted by
    /// <see cref="FlushPendingCleanups"/> so an application exit inside the cleanup window does
    /// not strand the credential the way a crash would.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingRdpCleanup> _pendingCleanups =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Prefix of every temporary .rdp artifact this handler creates.</summary>
    internal const string RdpArtifactFileNamePrefix = "heimdall_";

    /// <summary>Extension of every temporary .rdp artifact this handler creates.</summary>
    internal const string RdpArtifactFileExtension = ".rdp";

    /// <summary>
    /// Search pattern for the launch-time sweep. Derived from the same two constants the
    /// artifact name is built from, so producer and janitor cannot drift apart.
    /// </summary>
    internal const string RdpArtifactSearchPattern =
        RdpArtifactFileNamePrefix + "*" + RdpArtifactFileExtension;

    /// <summary>Upper bound on the number of files one launch-time sweep may examine.</summary>
    internal const int MaxSweptRdpArtifactsPerLaunch = 256;

    /// <summary>
    /// Age at which the launch-time sweep treats a temporary .rdp artifact as orphaned.
    /// Well above the 60 s ceiling the settings schema allows for
    /// RdpArtifactCleanupDelayMs, so a deferred cleanup still in flight is never raced.
    /// </summary>
    internal static readonly TimeSpan StaleRdpArtifactMaxAge = TimeSpan.FromHours(1);

    public RdpHandler(
        ITunnelService tunnelService,
        ConnectionStateMachine connectionSm,
        LocalizationManager localizer,
        IRdpExternalClientLauncher externalClientLauncher,
        ICredentialGuardService? credentialGuardService = null,
        IRdpCredentialManager? credentialManager = null,
        Func<string?, string?>? decryptPassword = null,
        RdpCredentialAutofillOperation? credentialAutofill = null,
        Action<string>? deleteRdpFile = null,
        Func<TimeSpan, Task>? artifactCleanupDelay = null,
        Action? sweepStaleRdpArtifacts = null)
    {
        _tunnelService = tunnelService;
        _connectionSm = connectionSm;
        _localizer = localizer;
        _externalClientLauncher = externalClientLauncher;
        _credentialGuardService = credentialGuardService ?? new CredentialGuardService();
        _credentialManager = credentialManager ?? new RdpCredentialManager();
        _decryptPassword = decryptPassword ?? ConnectionHelpers.DecryptPassword;
        _credentialAutofill = credentialAutofill
            ?? ((processId, host, secret, timeout, cancellationToken) =>
                Heimdall.Rdp.CredentialAutofill.WaitAndFillAsync(processId, host, secret, timeout, cancellationToken));
        _deleteRdpFile = deleteRdpFile ?? File.Delete;
        _artifactCleanupDelay = artifactCleanupDelay ?? (delay => Task.Delay(delay));
        _sweepStaleRdpArtifacts = sweepStaleRdpArtifacts ?? SweepStaleArtifactsBeforeLaunch;
    }

    /// <summary>
    /// Reclaims what earlier processes left behind: the temporary .rdp files in the temp
    /// directory and the Credential Manager entries written for them. Never throws.
    /// </summary>
    /// <remarks>
    /// Called at application startup, off the startup path, as well as before every external
    /// launch. The deferred cleanup only runs while Heimdall lives, and both halves of a launch
    /// outlive a crash; a stranded credential is the more expensive of the two, since it stays
    /// readable until the Windows session ends.
    /// </remarks>
    public static void SweepStaleArtifactsAtStartup()
    {
        SweepStaleRdpArtifactsInTempDirectory();
        SweepStaleOwnedCredentials();
    }

    private void SweepStaleArtifactsBeforeLaunch()
    {
        SweepStaleRdpArtifactsInTempDirectory();
        try
        {
            int deleted = _credentialManager.SweepStaleCredentials(Core.Logging.FileLogger.Warn);
            if (deleted > 0)
            {
                Core.Logging.FileLogger.Info($"Stale RDP launch sweep removed {deleted} orphaned Windows store entr{(deleted == 1 ? "y" : "ies")}");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Stale RDP credential sweep threw: {ex.GetType().FullName}");
        }
    }

    private static void SweepStaleOwnedCredentials()
    {
        try
        {
            int deleted = Heimdall.Rdp.CredentialManagerHelper.SweepStaleOwnedCredentials(Core.Logging.FileLogger.Warn);
            if (deleted > 0)
            {
                Core.Logging.FileLogger.Info($"Stale RDP launch sweep removed {deleted} orphaned Windows store entr{(deleted == 1 ? "y" : "ies")}");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Stale RDP credential sweep threw: {ex.GetType().FullName}");
        }
    }

    /// <summary>
    /// Runs every deferred cleanup now, for an application that is exiting.
    /// </summary>
    /// <remarks>
    /// The delayed cleanup is a task nobody awaits, so it dies with the process. Running it here
    /// costs the client that is still negotiating its credential, which the delay exists to
    /// protect; but the process is leaving and the alternative is a password left in the
    /// Credential Manager until logoff. Each entry is released once: the delayed task finds it
    /// gone and does nothing.
    /// </remarks>
    /// <returns>The number of cleanups run.</returns>
    public int FlushPendingCleanups()
    {
        int flushed = 0;
        foreach (string artifactPath in _pendingCleanups.Keys)
        {
            if (_pendingCleanups.TryRemove(artifactPath, out PendingRdpCleanup? pending))
            {
                RunCleanup(pending);
                flushed++;
            }
        }

        if (flushed > 0)
        {
            Core.Logging.FileLogger.Info($"RDP cleanup flushed {flushed} pending launch artifact(s) at exit");
        }

        return flushed;
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

        TunnelSetupOutcome tunnelOutcome = await _tunnelService.SetupTunnelIfNeededAsync(
                server,
                server.RemotePort,
                settings,
                ct,
                preferDistinctLoopback: !isEmbedded)
            .ConfigureAwait(false);
        var (tunnelOk, usesTunnel, targetHost, targetPort, tunnelError) = tunnelOutcome;

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

            // The route travels as text the tunnel layer composed when the tunnel was dialled,
            // not as a settings instance to be read later. The pane otherwise reads the gateway
            // list at materialisation time, which is a later instant and a different clone: a
            // gateway edited during the establishment delay then names one machine in the
            // certificate question while the certificate arrived from another.
            //
            // It is equally the answer for a REUSED tunnel, which an earlier connection opened
            // and this one only borrows. Carrying the settings this connection read would name
            // its own chain for a wire it did not dial; carrying nothing - which is what shipped
            // - left two identically named profiles reaching two different sites with no line at
            // all to tell them apart, which is the confusion the line exists to end.
            return new ConnectionResult(
                true,
                null,
                new RdpSessionResult(server, effectiveTunnelPort, tunnelOutcome.GatewayRoute));
        }

        string? rdpPassword = null;
        bool releaseTunnel = usesTunnel;
        string? warning = null;
        string? credentialCleanupTarget = null;
        string? credentialOwnershipMarker = null;
        string? rdpArtifactPath = null;
        bool deferredCleanupScheduled = false;
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
                }
                else
                {
                    warning = _localizer["RdpExistingWindowsCredentialNotice"];
                    rdpPassword = null;
                }
            }

            // The deferred cleanup below only runs while this process lives, so a crash or a
            // shutdown inside the cleanup window strands the artifact. Reclaim what earlier
            // runs left behind before adding one more file to %TEMP%.
            _sweepStaleRdpArtifacts();

            var rdpFile = Path.Combine(
                Path.GetTempPath(),
                $"{RdpArtifactFileNamePrefix}{server.Id}_{Guid.NewGuid():N}{RdpArtifactFileExtension}");
            // Take ownership of the artifact path as soon as it exists, so every early
            // return below deletes it synchronously before ConnectAsync hands back.
            rdpArtifactPath = rdpFile;
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
                            $"Failed to set ACL on .rdp file - file has inherited permissions: {aclEx.Message}");
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

            // Owned by the autofill task below, which disposes it when the fill returns.
            // Created before the Exited handler is registered so the handler can stop the
            // watcher the moment the client is gone.
            CancellationTokenSource? autofillCancellation =
                !string.IsNullOrEmpty(rdpPassword) && mstscPid > 0
                    ? new CancellationTokenSource()
                    : null;

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
                    // Once the client is gone the watcher can only keep sweeping the desktop
                    // with a live plaintext password, for a prompt that will never appear.
                    autofillCancellation?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The autofill task already finished and disposed its own source.
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"Autofill watcher cancellation on mstsc.exe exit failed: {ex.Message}");
                }

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

            if (autofillCancellation is not null)
            {
                // CredentialAutofill zeroes its char[] buffer after use, but this
                // closure string and its transient UI strings remain heap-resident
                // until GC; that is inherent to .NET immutable strings.
                string autofillPassword = rdpPassword!;
                CancellationTokenSource autofillCancellationForTask = autofillCancellation;
                // Autofill must outlive ConnectAsync: the connect-scoped token is cancelled
                // when ConnectAsync returns. It is bounded instead by its own timeout and by
                // this per-launch source, which the Exited handler above cancels.
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
                                autofillCancellationForTask.Token)
                            .ConfigureAwait(false);
                        if (!filled)
                        {
                            Core.Logging.FileLogger.Warn(
                                $"External RDP CredUI autofill timed out for {server.DisplayName}");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected: the external client exited before the prompt appeared, or
                        // the fill already completed. Not logged - the Info/Debug regime bans
                        // this vocabulary, and the client exit is already recorded above.
                    }
                    catch (Exception ex)
                    {
                        Core.Logging.FileLogger.Warn($"External RDP CredUI autofill failed: {ex.Message}");
                    }
                    finally
                    {
                        // The fill has returned: nothing else may scan the desktop with
                        // this password, and the source has no other owner.
                        autofillCancellationForTask.Cancel();
                        autofillCancellationForTask.Dispose();
                    }
                }, CancellationToken.None);
            }

            var cleanupDelay = TimeSpan.FromMilliseconds(settings.RdpArtifactCleanupDelayMs);

            // Registered here, before the task starts, so an exit flush that runs between this
            // return and the task's first instruction still finds the entry to release.
            PendingRdpCleanup pendingCleanup = new(rdpFile, credentialCleanupTarget, credentialOwnershipMarker);
            _pendingCleanups[rdpFile] = pendingCleanup;
            _ = Task.Run(async () =>
            {
                try
                {
                    await CleanupRdpArtifactsAsync(pendingCleanup, cleanupDelay).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"RDP cleanup failed: {ex.Message}");
                }
            }, CancellationToken.None);
            deferredCleanupScheduled = true;

            return new ConnectionResult(true, null, null, Warning: warning);
        }
        catch (Heimdall.Rdp.RdpGatewayAttestationException gex)
        {
            Core.Logging.FileLogger.Error("RDP gateway attestation failed", gex);
            return new ConnectionResult(
                false,
                _localizer["RdpGatewayAttestationFailed"],
                null,
                RdpSessionDiagnosticFactory.FromGenericException(gex));
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
            if (!deferredCleanupScheduled)
            {
                // Both helpers are non-throwing, so neither cleanup can be starved by
                // the other and neither can mask the ConnectionResult built above.
                if (credentialCleanupTarget is not null &&
                    credentialOwnershipMarker is not null)
                {
                    ReleaseOwnedCredential(credentialCleanupTarget, credentialOwnershipMarker);
                }

                if (rdpArtifactPath is not null)
                {
                    DeleteRdpArtifact(rdpArtifactPath);
                }
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
    /// Cleans up the temporary .rdp file and CredMan entry after a delay. The delay is
    /// deliberately not bound to the connect-scoped token: that token is cancelled when the
    /// command is re-executed, and cancelling it used to bring the deletion forward instead
    /// of holding it back, pulling the credential out from under a client still negotiating.
    /// </summary>
    private async Task CleanupRdpArtifactsAsync(PendingRdpCleanup pending, TimeSpan cleanupDelay)
    {
        await _artifactCleanupDelay(cleanupDelay).ConfigureAwait(false);

        // Whoever removes the entry runs the cleanup, so an exit flush that got there first
        // leaves nothing for this task to do.
        if (_pendingCleanups.TryRemove(pending.RdpFile, out _))
        {
            RunCleanup(pending);
        }
    }

    private void RunCleanup(PendingRdpCleanup pending)
    {
        DeleteRdpArtifact(pending.RdpFile);

        if (pending.CredentialTarget is not null && pending.OwnershipMarker is not null)
        {
            ReleaseOwnedCredential(pending.CredentialTarget, pending.OwnershipMarker);
        }
    }

    /// <summary>One launch's artifacts awaiting their deferred cleanup.</summary>
    private sealed record PendingRdpCleanup(
        string RdpFile,
        string? CredentialTarget,
        string? OwnershipMarker);

    private static void SweepStaleRdpArtifactsInTempDirectory()
    {
        SweepStaleRdpArtifacts(
            Path.GetTempPath(),
            StaleRdpArtifactMaxAge,
            DateTime.UtcNow,
            File.Delete);
    }

    /// <summary>
    /// Removes temporary .rdp artifacts an earlier process left behind. The deferred
    /// cleanup only runs while Heimdall lives, so a crash or a shutdown inside the cleanup
    /// window strands the file; this bounded sweep is the janitor that reclaims it. Never
    /// throws, and only touches files older than <paramref name="maxAge"/> so a cleanup
    /// still in flight - in this process or another instance - is left alone.
    /// </summary>
    /// <returns>The number of artifacts deleted.</returns>
    internal static int SweepStaleRdpArtifacts(
        string directory,
        TimeSpan maxAge,
        DateTime utcNow,
        Action<string> deleteFile)
    {
        ArgumentNullException.ThrowIfNull(deleteFile);

        int deleted = 0;
        int examined = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, RdpArtifactSearchPattern))
            {
                if (examined >= MaxSweptRdpArtifactsPerLaunch)
                {
                    break;
                }

                examined++;
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(path) < maxAge)
                    {
                        continue;
                    }

                    deleteFile(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn(
                        $"Stale RDP artifact sweep: failed to delete '{path}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"Stale RDP artifact sweep failed: {ex.Message}");
        }

        if (deleted > 0)
        {
            Core.Logging.FileLogger.Info($"Stale RDP artifact sweep removed {deleted} orphaned file(s)");
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the temporary .rdp artifact. Never throws: a failed deletion -
    /// including <see cref="UnauthorizedAccessException"/> - must never prevent the
    /// owned CredMan entry from being released. The .rdp file carries no secret,
    /// and the log line records only the path and the exception message.
    /// </summary>
    private void DeleteRdpArtifact(string rdpFile)
    {
        try
        {
            _deleteRdpFile(rdpFile);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RDP artifact cleanup: failed to delete temp .rdp file '{rdpFile}': {ex.Message}");
        }
    }

    /// <summary>
    /// Releases the CredMan entry Heimdall owns. Never throws: an unexpected provider
    /// failure must neither mask the <see cref="ConnectionResult"/> nor prevent the
    /// temporary .rdp artifact from being deleted. Only the exception type is logged -
    /// never a username, domain, password, ownership marker or credential blob.
    /// </summary>
    private void ReleaseOwnedCredential(string credentialTarget, string ownershipMarker)
    {
        try
        {
            bool operationSucceeded = _credentialManager.DeleteCredential(
                credentialTarget,
                ownershipMarker,
                out _,
                out string? credentialError);
            if (!operationSucceeded)
            {
                Core.Logging.FileLogger.Warn(
                    $"RDP CredMan cleanup failed for {credentialTarget}: {credentialError ?? "unknown error"}");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn(
                $"RDP CredMan cleanup threw for {credentialTarget}: {ex.GetType().FullName}");
        }
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

    /// <summary>
    /// Deletes the entries an earlier launch wrote and never released. Returns how many.
    /// </summary>
    int SweepStaleCredentials(Action<string>? warn);
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

    public int SweepStaleCredentials(Action<string>? warn)
    {
        return Heimdall.Rdp.CredentialManagerHelper.SweepStaleOwnedCredentials(warn);
    }
}
