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

    public static TimeSpan ResolveConnectTimeExitWindow(int? configuredSeconds)
    {
        return configuredSeconds.HasValue
            ? TimeSpan.FromSeconds(configuredSeconds.Value)
            : ConnectTimeExitWindow;
    }
}
