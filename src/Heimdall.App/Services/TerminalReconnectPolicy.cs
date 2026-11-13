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

using Heimdall.App.Localization;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Decides whether a process-backed terminal session should auto-reconnect when its
/// process exits with a non-zero code. Connection-backed protocols reconnect; local
/// or handed-off process protocols do not.
/// </summary>
internal static class TerminalReconnectPolicy
{
    internal static readonly TimeSpan ConnectTimeExitWindow = TimeSpan.FromSeconds(15);

    public static bool ReconnectsOnProcessExit(string? connectionType)
    {
        // Local / handed-off process sessions: a non-zero exit means the process ended
        // or auth failed, never a transient drop, so do not auto-reconnect.
        if (string.Equals(connectionType, "LOCAL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Everything else (SSH-via-plink, Telnet, unknown) is treated as a network
        // session that may have dropped, so auto-reconnect stays enabled.
        return true;
    }

    /// <summary>
    /// Locale key of the generic explanation shown when a WinRM tab's PowerShell host ends
    /// without the remote session ever having been entered, or null for any other exit.
    /// Whatever PowerShell printed above is untranslated and unstructured, so this is the
    /// one actionable line every such failure gets, specific diagnostics aside.
    /// </summary>
    public static string? ResolveWinRmNotEnteredMessageKey(string? connectionType, int exitCode)
        => string.Equals(connectionType, "WINRM", StringComparison.OrdinalIgnoreCase)
            && exitCode == Heimdall.App.Services.WinRm.WinRmPowerShellLaunchBuilder.RemoteSessionNotEnteredExitCode
                ? "ErrorWinRmSessionNotEntered"
                : null;

    /// <summary>
    /// Locale key of the hint shown when a session launched without any credential (Kerberos or
    /// a Pageant key) ended with a failure inside the connect-time window, or null otherwise.
    /// <paramref name="connectTimeExit"/> is the result of
    /// <see cref="SuppressesConnectTimeProcessExit"/>, so the window is defined in one place.
    /// A user-initiated disconnect kills the process with a non-zero code, which is not a
    /// server refusal, so it never gets the hint.
    /// </summary>
    public static string? ResolveCredentialFreeExitMessageKey(
        bool credentialFree,
        int exitCode,
        bool connectTimeExit,
        bool userInitiatedDisconnect)
        => credentialFree && exitCode != 0 && connectTimeExit && !userInitiatedDisconnect
            ? SshLocalizationKeys.ErrorSshPasswordlessSignInFailed
            : null;

    /// <summary>
    /// Text of the disconnected panel's detail block: the resolved reason first, then the
    /// generic disconnect detail (the exit code) on a second line. Either part may be absent.
    /// </summary>
    public static string? ComposePanelDetail(string? reason, string? disconnectDetail)
    {
        bool hasReason = !string.IsNullOrWhiteSpace(reason);
        bool hasDetail = !string.IsNullOrWhiteSpace(disconnectDetail);
        if (hasReason && hasDetail)
        {
            return reason + "\n" + disconnectDetail;
        }

        return hasReason ? reason : hasDetail ? disconnectDetail : null;
    }

    /// <summary>
    /// Classifies a terminal process exit into a disconnect info: exit code 0 is a clean
    /// end; a non-zero exit is auto-reconnect-eligible only when the session is
    /// connection-backed, otherwise it is surfaced but not reconnected.
    /// </summary>
    public static SshSessionDisconnectInfo ClassifyProcessExit(
        int exitCode,
        bool autoReconnectOnProcessExit,
        bool suppressAutoReconnect = false)
    {
        // The detail is named by locale key and carries the exit code; the view formats it,
        // so the terminal marker reads in the user's language instead of composed English.
        SshSessionDisconnectInfo disconnect;
        if (exitCode == 0)
        {
            disconnect = SshSessionDisconnectInfo.Clean();
        }
        else
        {
            disconnect = autoReconnectOnProcessExit && !suppressAutoReconnect
                ? SshSessionDisconnectInfo.Unclassified(null)
                : SshSessionDisconnectInfo.TerminalEnded(null);
        }

        return disconnect.WithMessageKey(SshLocalizationKeys.SshDisconnectProcessExited, exitCode);
    }

    public static bool SuppressesConnectTimeProcessExit(
        string? connectionType,
        bool isPipeModeSession,
        bool hasTerminalInput,
        TimeSpan processRuntime,
        TimeSpan? connectTimeExitWindow = null)
    {
        TimeSpan effectiveExitWindow = connectTimeExitWindow ?? ConnectTimeExitWindow;

        return string.Equals(connectionType, "SSH", StringComparison.OrdinalIgnoreCase)
            && isPipeModeSession
            && !hasTerminalInput
            && processRuntime >= TimeSpan.Zero
            && processRuntime <= effectiveExitWindow;
    }

    /// <summary>
    /// Whether a shell session that ended <paramref name="sinceAttach"/> after it was attached
    /// should skip auto-reconnect. A device that closes right after opening the shell (a session
    /// limit, an access list) would otherwise be retried at the shortest delay forever, because
    /// every attach restarts the attempt count. A session the user typed into is a real session.
    /// </summary>
    public static bool SuppressesConnectTimeShellDisconnect(
        bool hasTerminalInput,
        TimeSpan sinceAttach,
        TimeSpan? connectTimeExitWindow = null)
    {
        TimeSpan effectiveExitWindow = connectTimeExitWindow ?? ConnectTimeExitWindow;

        return !hasTerminalInput
            && sinceAttach >= TimeSpan.Zero
            && sinceAttach <= effectiveExitWindow;
    }

    public static TimeSpan ResolveConnectTimeExitWindow(int? configuredSeconds)
    {
        return configuredSeconds.HasValue
            ? TimeSpan.FromSeconds(configuredSeconds.Value)
            : ConnectTimeExitWindow;
    }
}
