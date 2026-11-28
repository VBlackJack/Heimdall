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
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.Pageant;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services.Handlers;

internal sealed partial class SshHandler
{
    /// <summary>
    /// Refuses, before the tunnel and the dial, a session whose chosen agent key cannot be used.
    /// </summary>
    /// <returns>
    /// The refusal (null when the connection may proceed) and the key found in any agent (null
    /// when none is chosen).
    /// </returns>
    private async Task<(ConnectionResult? Refusal, SshAgentIdentityMatch? Chosen)> PreflightChosenAgentKeyAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct)
    {
        if (server.SshAgentKeyFingerprint is not { } fingerprint)
        {
            return (null, null);
        }

        // An external PuTTY has no way to be told which agent key to use.
        if (string.Equals(server.SshMode, "External", StringComparison.OrdinalIgnoreCase))
        {
            return (RefuseAgentKey(server, SshAgentKeyRefusal.LauncherUnsupported, fingerprint), null);
        }

        SshAgentRegistry registry = _agentRegistryFactory(settings.SshAgentPreference);
        SshAgentIdentityMatch? chosen = await SshAgentKeyPreflight
            .LocateAsync(fingerprint, registry, ct: ct)
            .ConfigureAwait(false);
        if (chosen is null)
        {
            return (RefuseAgentKey(server, SshAgentKeyRefusal.NotLoaded, fingerprint), null);
        }

        // Forwarding sends the connection through Plink, which only reaches Pageant. Whether
        // Pageant holds the key and can describe it in a file Plink reads is judged here; the key
        // file and the launcher need the plink path, so they are judged where it is resolved.
        if (server.SshAgentForwarding && registry.HasPlinkCompatibleAgent())
        {
            SshAgentIdentityMatch? pageantKey = await SshAgentKeyPreflight
                .LocateAsync(fingerprint, registry, PageantAgent.AgentName, ct: ct)
                .ConfigureAwait(false);
            SshAgentKeyRefusal refusal = SshAgentKeyPreflight.RefusalForPlink(
                pageantKey,
                keyFileConfigured: false,
                plinkAttested: true);
            if (refusal != SshAgentKeyRefusal.None)
            {
                return (RefuseAgentKey(server, refusal, fingerprint), null);
            }
        }

        return (null, chosen);
    }

    /// <summary>
    /// Whether a retry through Plink would offer the chosen key and nothing else.
    /// </summary>
    private async Task<bool> CanPlinkHonourChosenKeyAsync(
        ServerProfileDto server,
        AppSettings settings,
        CancellationToken ct)
    {
        string? plinkPath = ConnectionHelpers.ResolvePlinkPath(settings.PlinkPath);
        if (string.IsNullOrWhiteSpace(plinkPath) || !File.Exists(plinkPath) || server.SshAgentKeyFingerprint is null)
        {
            return false;
        }

        SshAgentIdentityMatch? pageantKey = await SshAgentKeyPreflight
            .LocateAsync(
                server.SshAgentKeyFingerprint,
                _agentRegistryFactory(settings.SshAgentPreference),
                PageantAgent.AgentName,
                ct: ct)
            .ConfigureAwait(false);
        return SshAgentKeyPreflight.RefusalForPlink(
            pageantKey,
            keyFileConfigured: !string.IsNullOrWhiteSpace(server.SshKeyPath),
            plinkAttested: IsPlinkAttested(plinkPath)) == SshAgentKeyRefusal.None;
    }

    /// <summary>What the launch needs, built under a guard that frees everything on any failure.</summary>
    private sealed record PlinkLaunchPreparation(
        string Args,
        PlinkPasswordFileReleaseHandle? PasswordFileRelease,
        PlinkSizeSessionReleaseHandle? SizeSessionRelease,
        PlinkPasswordFileReleaseHandle? AgentKeyFileRelease,
        Exception? Error);

    /// <summary>
    /// Writes the password file, creates the size session, builds the command line and writes the
    /// agent public key file, arming each release as it goes.
    /// </summary>
    /// <remarks>
    /// Any exception frees every file created so far and disposes the session, then comes back as
    /// <see cref="PlinkLaunchPreparation.Error"/>: the password file holds a secret and must not
    /// outlive a failure between its creation and the launch. A method of its own so the launch
    /// catches of the caller keep their shape.
    /// </remarks>
    private PlinkLaunchPreparation PrepareLaunch(
        ServerProfileDto server,
        string? password,
        TerminalSize initialSize,
        bool x11Forwarding,
        int targetPort,
        string target,
        string? hostKeyArg,
        SshAgentIdentityMatch? chosenAgentKey,
        Heimdall.Terminal.PipeModeSession terminalSession,
        bool firstByteProvesConsumption,
        bool credentialFree,
        bool usesTunnel = false)
    {
        string? passwordFilePath = null;
        string? sizeSessionName = null;
        string? agentKeyFilePath = null;
        PlinkPasswordFileReleaseHandle? passwordFileRelease = null;
        PlinkSizeSessionReleaseHandle? sizeSessionRelease = null;
        PlinkPasswordFileReleaseHandle? agentKeyFileRelease = null;
        try
        {
            passwordFilePath = CreatePlinkPasswordFile(password);

            // A temporary saved session is the only way the size reaches Plink. A null name
            // means the registry refused and the launch goes ahead at Plink's default size.
            sizeSessionName = PlinkSizeSession.TryCreate(
                _puttySessionRegistry,
                initialSize.Columns,
                initialSize.Rows);

            agentKeyFilePath = chosenAgentKey is null ? null : PlinkAgentKeyFile.NewPath();

            string args = BuildPipeModeArguments(
                server.SshKeyPath,
                server.SshCompression,
                server.SshAgentForwarding,
                x11Forwarding,
                targetPort,
                target,
                hostKeyArg,
                passwordFilePath,
                sizeSessionName,
                agentKeyFilePath,
                credentialFree,
                localForwards: server.SshLocalForwards,
                socksProxyPort: usesTunnel ? 0 : server.SocksProxyPort,
                remoteBindPort: usesTunnel ? 0 : server.RemoteBindPort,
                remoteLocalPort: usesTunnel ? 0 : server.RemoteLocalPort);

            if (!string.IsNullOrEmpty(passwordFilePath))
            {
                // Armed before the process starts so no output can arrive unobserved. Every path
                // that frees this file goes through the returned handle, including the catches
                // of the caller: disposing the session can itself raise ProcessExited, so a direct
                // delete there would be a second invocation.
                passwordFileRelease = PlinkPasswordFileRelease.Arm(
                    terminalSession,
                    passwordFilePath,
                    _deletePlinkPasswordFile,
                    firstByteProvesConsumption);

                // Disposing kills the process and detaches the exit notification, so a tab closed
                // on a normal path would otherwise leave the plaintext password file behind until
                // the janitor. The handle deletes once whichever signal arrives first.
                PlinkPasswordFileReleaseHandle passwordHandle = passwordFileRelease;
                terminalSession.Disposed += passwordHandle.Release;
            }

            // Same single release path and the same proof as the password file: -load is read
            // in the command-line pass, so the first byte shows the session was loaded. Also
            // release on Disposed so closing a tab before output never leaves a registry key behind.
            if (sizeSessionName is not null)
            {
                sizeSessionRelease = PlinkSizeSession.ArmRelease(
                    terminalSession,
                    sizeSessionName,
                    name => PlinkSizeSession.Delete(_puttySessionRegistry, name),
                    firstByteProvesConsumption);
                PlinkSizeSessionReleaseHandle sizeHandle = sizeSessionRelease;
                terminalSession.Disposed += sizeHandle.Release;
            }

            // The public key file is written last, once the other releases are armed. It is
            // freed on process exit and also when the session is disposed: disposing kills the
            // process and detaches the exit notification, so a closed tab would otherwise leave
            // it behind. The key is read when authentication starts, not when the command line is
            // parsed, so the first byte of output proves nothing about it.
            if (chosenAgentKey is not null && agentKeyFilePath is not null)
            {
                agentKeyFileRelease = PlinkPasswordFileRelease.Arm(
                    terminalSession,
                    agentKeyFilePath,
                    _deletePlinkPasswordFile,
                    firstByteProvesConsumption: false);
                PlinkPasswordFileReleaseHandle keyFileHandle = agentKeyFileRelease;
                terminalSession.Disposed += keyFileHandle.Release;
                _writeAgentKeyFile(agentKeyFilePath, chosenAgentKey.Key);
            }

            return new PlinkLaunchPreparation(args, passwordFileRelease, sizeSessionRelease, agentKeyFileRelease, null);
        }
        catch (Exception ex)
        {
            terminalSession.Dispose();

            // A file created but not yet armed has no handle: freed directly. An armed one is freed
            // through its handle, which deletes at most once.
            if (passwordFileRelease is not null)
            {
                passwordFileRelease.Release();
            }
            else if (passwordFilePath is not null)
            {
                _deletePlinkPasswordFile(passwordFilePath);
            }

            if (sizeSessionRelease is not null)
            {
                sizeSessionRelease.Release();
            }
            else if (sizeSessionName is not null)
            {
                PlinkSizeSession.Delete(_puttySessionRegistry, sizeSessionName);
            }

            if (agentKeyFileRelease is not null)
            {
                agentKeyFileRelease.Release();
            }
            else if (agentKeyFilePath is not null)
            {
                _deletePlinkPasswordFile(agentKeyFilePath);
            }

            return new PlinkLaunchPreparation(string.Empty, null, null, null, ex);
        }
    }

    private bool IsPlinkAttested(string? plinkPath)
    {
        using PlinkAttestationLease lease = _plinkAttestation(plinkPath);
        return lease.FirstByteProvesConsumption;
    }

    private ConnectionResult RefuseAgentKey(
        ServerProfileDto server,
        SshAgentKeyRefusal refusal,
        string fingerprint,
        string? originalFailureMessage = null)
    {
        string key = SshAgentKeyPreflight.MessageKey(refusal);
        string refusalMessage = SshAgentKeyPreflight.NamesFingerprint(refusal)
            ? _localizer.Format(key, fingerprint)
            : _localizer[key];

        // After a refusal the embedded client already met, its sentence stays first.
        string message = string.IsNullOrWhiteSpace(originalFailureMessage)
            ? refusalMessage
            : SshAuthFailureMessageComposer.AppendSentence(originalFailureMessage, refusalMessage, key)
                ?? refusalMessage;

        Core.Logging.FileLogger.Warn(
            $"SSH connection of {server.DisplayName} refused: agent key {fingerprint}: {refusal}");
        _connectionSm.SetError(server.Id, message);
        return new ConnectionResult(
            false,
            message,
            null,
            SshSessionDiagnosticFactory.CreatePreflightFailure(
                key,
                message,
                refusal == SshAgentKeyRefusal.LauncherUnsupported ? null : SshFailureCode.AgentKeyNotLoaded));
    }

    private static string BuildDisplayEndpoint(ServerProfileDto server)
    {
        var user = string.IsNullOrWhiteSpace(server.SshUsername) ? "?" : server.SshUsername;
        var host = string.IsNullOrWhiteSpace(server.RemoteServer) ? "?" : server.RemoteServer;
        var port = server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh;
        return $"{user}@{host}:{port}";
    }

    /// <summary>
    /// Whether this profile would put a password on disk for a launcher that has no login name.
    /// </summary>
    /// <remarks>
    /// <para>Password-backed means either a stored password - with or without a key - or neither a
    /// password nor a key, since that path goes on to ask for a password. A profile with a key and
    /// no password never writes the file, so it is left alone.</para>
    /// <para>Whitespace counts as absent: the launcher would receive a bare host either way.</para>
    /// </remarks>
    internal static bool RequiresUsernameBeforeWritingPassword(ServerProfileDto server)
    {
        if (!string.IsNullOrWhiteSpace(server.SshUsername))
        {
            return false;
        }

        bool hasStoredPassword = !string.IsNullOrEmpty(server.SshPasswordEncrypted);
        bool hasKey = !string.IsNullOrWhiteSpace(server.SshKeyPath);

        return hasStoredPassword || !hasKey;
    }

    internal static bool ShouldPromptForPlinkPassword(string? password, string? keyPath)
    {
        // Takes the password rather than the file that would hold it: the file is created after the
        // dialog now, so asking about it here would mean writing the secret to disk before knowing
        // whether the user was even going to supply one.
        return string.IsNullOrEmpty(password) && string.IsNullOrWhiteSpace(keyPath);
    }

    private static (string Host, int Port) ResolvePlinkHostKeyIdentity(
        ServerProfileDto server,
        string transportHost,
        int transportPort,
        bool usesTunnel)
    {
        return usesTunnel
            ? (server.RemoteServer, server.SshPort > 0 ? server.SshPort : DefaultPorts.Ssh)
            : (transportHost, transportPort);
    }

    private static string? CreatePlinkPasswordFile(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var passwordFilePath = Path.Combine(
            Path.GetTempPath(),
            $"{PlinkPasswordFileNaming.Prefix}{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            SecureFileWriter.WriteAndProtect(passwordFilePath, password);
        }
        else
        {
            File.WriteAllText(passwordFilePath, password);
            try
            {
                File.SetUnixFileMode(
                    passwordFilePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                Core.Logging.FileLogger.Warn(
                    $"[SshHandler] Unix file mode restriction failed for Plink password file: {ex.Message}");
            }
        }

        return passwordFilePath;
    }

    private static void DeletePlinkPasswordFile(string? passwordFilePath)
    {
        if (string.IsNullOrWhiteSpace(passwordFilePath))
        {
            return;
        }

        try
        {
            File.Delete(passwordFilePath);
        }
        catch (IOException ex)
        {
            Core.Logging.FileLogger.Warn($"[SshHandler] DeletePlinkPasswordFile: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Core.Logging.FileLogger.Warn($"[SshHandler] DeletePlinkPasswordFile: {ex.Message}");
        }
    }

    internal static System.Diagnostics.ProcessStartInfo BuildPuttyStartInfo(
        string puttyPath,
        string? keyPath,
        bool compression,
        bool agentForwarding,
        bool x11Forwarding,
        int port,
        string target,
        string? hostKeyFingerprint,
        IReadOnlyList<string>? localForwards = null,
        int socksProxyPort = 0,
        int remoteBindPort = 0,
        int remoteLocalPort = 0)
    {
        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = puttyPath,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("-ssh");
        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(keyPath);
        }

        if (compression)
        {
            psi.ArgumentList.Add("-C");
        }

        if (agentForwarding)
        {
            psi.ArgumentList.Add("-A");
        }

        if (x11Forwarding)
        {
            psi.ArgumentList.Add("-X");
        }

        AppendPuttyForwardArguments(psi.ArgumentList, localForwards, socksProxyPort, remoteBindPort, remoteLocalPort);

        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(port.ToString());

        if (!string.IsNullOrWhiteSpace(hostKeyFingerprint))
        {
            psi.ArgumentList.Add("-hostkey");
            psi.ArgumentList.Add(hostKeyFingerprint);
        }

        psi.ArgumentList.Add(target);
        return psi;
    }

    internal static string BuildPipeModeArguments(
        string? keyPath,
        bool compression,
        bool agentForwarding,
        bool x11Forwarding,
        int port,
        string target,
        string? hostKeyFingerprint,
        string? passwordFilePath = null,
        string? sizeSessionName = null,
        string? agentPublicKeyFilePath = null,
        bool batchMode = false,
        IReadOnlyList<string>? localForwards = null,
        int socksProxyPort = 0,
        int remoteBindPort = 0,
        int remoteLocalPort = 0)
    {
        // Plink takes one identity file. Both set means two things asked for the same slot, which
        // the handler refuses before building: reaching here is a programming error.
        if (!string.IsNullOrWhiteSpace(keyPath) && !string.IsNullOrWhiteSpace(agentPublicKeyFilePath))
        {
            throw new ArgumentException(
                "A private key file and an agent public key file cannot both take the -i slot.",
                nameof(agentPublicKeyFilePath));
        }

        var argParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(sizeSessionName))
        {
            // First, so every explicit flag below reads as applied on top of the loaded session.
            // Plink replays its explicit options after -load whatever the order (measured: the
            // size and -hostkey hold at any position); the order only keeps that obvious.
            argParts.Add("-load");
            argParts.Add(sizeSessionName);
        }

        argParts.AddRange(["-ssh", "-t", "-no-antispoof"]);
        if (batchMode)
        {
            // Plink cannot answer a prompt in batch mode, so a refused sign-in exits with code 1
            // instead of waiting forever on a console nobody can see.
            argParts.Add("-batch");
        }

        if (!string.IsNullOrWhiteSpace(keyPath))
        {
            argParts.Add("-i");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(keyPath)}\"");
        }
        else if (!string.IsNullOrWhiteSpace(agentPublicKeyFilePath))
        {
            // A public key file given to -i makes Pageant offer that key (PuTTY documents it).
            // Nothing private is read from this file.
            argParts.Add("-i");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(agentPublicKeyFilePath)}\"");
        }

        if (compression)
        {
            argParts.Add("-C");
        }

        if (agentForwarding)
        {
            argParts.Add("-A");
        }

        if (x11Forwarding)
        {
            argParts.Add("-X");
        }

        AppendPuttyForwardArguments(argParts, localForwards, socksProxyPort, remoteBindPort, remoteLocalPort);

        argParts.Add("-P");
        argParts.Add(port.ToString());

        if (!string.IsNullOrWhiteSpace(hostKeyFingerprint))
        {
            argParts.Add("-hostkey");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(hostKeyFingerprint)}\"");
        }

        if (!string.IsNullOrWhiteSpace(passwordFilePath))
        {
            argParts.Add("-pwfile");
            argParts.Add($"\"{InputValidator.EscapeForDoubleQuotedString(passwordFilePath)}\"");
        }

        argParts.Add(target);
        return string.Join(' ', argParts);
    }

    private static void AppendPuttyForwardArguments(
        ICollection<string> args,
        IReadOnlyList<string>? localForwards,
        int socksProxyPort,
        int remoteBindPort,
        int remoteLocalPort)
    {
        if (localForwards is { Count: > 0 })
        {
            foreach (var raw in localForwards)
            {
                if (!SshLocalForwardSpec.TryParse(raw, out var spec) || spec is null)
                {
                    continue;
                }

                string bindHost = spec.BindAddress.Contains(':', StringComparison.Ordinal)
                    ? $"[{spec.BindAddress}]"
                    : spec.BindAddress;
                string remoteHost = spec.RemoteHost.Contains(':', StringComparison.Ordinal)
                    ? $"[{spec.RemoteHost}]"
                    : spec.RemoteHost;
                args.Add("-L");
                args.Add($"{bindHost}:{spec.LocalPort}:{remoteHost}:{spec.RemotePort}");
            }
        }

        if (socksProxyPort is >= 1 and <= 65535)
        {
            args.Add("-D");
            args.Add($"{LoopbackBinding.DefaultHost}:{socksProxyPort}");
        }

        if (remoteBindPort is >= 1 and <= 65535)
        {
            int localTarget = remoteLocalPort is >= 1 and <= 65535 ? remoteLocalPort : remoteBindPort;
            args.Add("-R");
            args.Add($"{LoopbackBinding.DefaultHost}:{remoteBindPort}:{LoopbackBinding.DefaultHost}:{localTarget}");
        }
    }

    internal static bool TryValidateKeyPath(
        string? keyPath,
        out SshKeyPathValidationError error)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            error = SshKeyPathValidationError.None;
            return true;
        }

        if (keyPath.Contains('\0') || keyPath.Contains('"'))
        {
            error = SshKeyPathValidationError.InvalidCharacters;
            return false;
        }

        if (!Path.IsPathRooted(keyPath))
        {
            error = SshKeyPathValidationError.NotAbsolute;
            return false;
        }

        if (!File.Exists(keyPath))
        {
            error = SshKeyPathValidationError.FileNotFound;
            return false;
        }

        error = SshKeyPathValidationError.None;
        return true;
    }

    private string LocalizeKeyPathError(
        SshKeyPathValidationError error,
        string? keyPath)
    {
        string displayPath = keyPath ?? string.Empty;
        return error switch
        {
            SshKeyPathValidationError.InvalidCharacters =>
                _localizer.Format(SshLocalizationKeys.ErrorSshKeyPathInvalid, displayPath),
            SshKeyPathValidationError.NotAbsolute =>
                _localizer.Format(SshLocalizationKeys.ErrorSshKeyPathNotAbsolute, displayPath),
            SshKeyPathValidationError.FileNotFound =>
                _localizer.Format(SshLocalizationKeys.ErrorSshKeyFileNotFound, displayPath),
            _ => string.Empty,
        };
    }

    private ConnectionResult BuildPlinkHostKeyRejectionResult(
        string serverId,
        string targetHost,
        int targetPort,
        PlinkHostKeyDecision decision)
    {
        if (decision.FailureCode == SshFailureCode.HostKeyRevoked)
        {
            string revokedMessage = SshFailureMessageBuilder.HostKeyRevoked(_localizer);
            _connectionSm.SetError(serverId, revokedMessage);
            return new ConnectionResult(
                false,
                revokedMessage,
                null,
                SshSessionDiagnosticFactory.CreateHostKeyRevokedFailure(
                    decision.PresentedFingerprint ?? string.Empty,
                    targetHost,
                    targetPort));
        }

        if (decision.FailureCode == SshFailureCode.HostKeyMismatch
            && decision.StoredFingerprint is not null
            && decision.PresentedFingerprint is not null)
        {
            string message = SshFailureMessageBuilder.HostKeyMismatch(
                _localizer,
                decision.StoredFingerprint,
                decision.PresentedFingerprint);
            _connectionSm.SetError(serverId, message);
            return new ConnectionResult(
                false,
                message,
                null,
                SshSessionDiagnosticFactory.CreateHostKeyMismatchFailure(
                    decision.StoredFingerprint,
                    decision.PresentedFingerprint,
                    targetHost,
                    targetPort));
        }

        if (decision.FailureCode == SshFailureCode.Cancelled)
        {
            string message = SshFailureMessageBuilder.Cancelled(_localizer);
            _connectionSm.SetError(serverId, message);
            return new ConnectionResult(
                false,
                message,
                null,
                SshSessionDiagnosticFactory.CreatePlinkFallbackFailure(
                    SshLocalizationKeys.ErrorSshCancelled,
                    message,
                    SshFailureCode.Cancelled));
        }

        string unavailableMessage = SshFailureMessageBuilder.HostKeyUnavailable(_localizer);
        _connectionSm.SetError(serverId, unavailableMessage);
        return new ConnectionResult(
            false,
            unavailableMessage,
            null,
            SshSessionDiagnosticFactory.CreateHostKeyUnavailableFailure(
                unavailableMessage,
                targetHost,
                targetPort));
    }
}
