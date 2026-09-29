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

using Heimdall.Core.Logging;
using Heimdall.Ssh.Plink;
using Heimdall.Terminal;
using Microsoft.Win32;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Creates the temporary PuTTY saved session through which the initial terminal size reaches a
/// pipe-mode Plink, and arms its deletion.
/// </summary>
/// <remarks>
/// <para>Windows Plink builds its <c>pty-req</c> from <c>TermWidth</c>/<c>TermHeight</c> in its
/// configuration and nothing else: not from a console, which a pipe-mode child does not have, and
/// it never sends <c>window-change</c>. Measured against PuTTY 0.83: <c>-load</c> of a session
/// carrying 173x41 gives a remote <c>stty size</c> of <c>41 173</c>, whatever the position of
/// <c>-load</c> among the arguments, and <c>-hostkey</c> still wins over the loaded session.</para>
/// <para><c>-load</c> replaces "Default Settings" entirely: values missing from the loaded
/// session fall back to PuTTY's compiled-in defaults. So every value of "Default Settings" is
/// copied first, and only then are the two size values set, to keep the launch identical to one
/// without <c>-load</c> apart from the size.</para>
/// <para><c>HostName</c> is never written, even when "Default Settings" carries one: a session
/// without it is not launchable, which keeps it off the Windows jump list and out of any
/// launcher that lists saved sessions.</para>
/// <para>Fail-open for the size only. The session is a convenience for the terminal width; if the
/// registry refuses any step, a warning is logged and Plink is launched exactly as before, at its
/// default size, rather than failing the connection.</para>
/// </remarks>
internal static class PlinkSizeSession
{
    /// <summary>PuTTY's configuration value for the terminal width, in columns.</summary>
    internal const string TermWidthValueName = "TermWidth";

    /// <summary>PuTTY's configuration value for the terminal height, in rows.</summary>
    internal const string TermHeightValueName = "TermHeight";

    /// <summary>PuTTY's configuration value naming the host; never written to a size session.</summary>
    internal const string HostNameValueName = "HostName";

    /// <summary>
    /// Creates a size session for <paramref name="columns"/> x <paramref name="rows"/> and returns
    /// its name, or <see langword="null"/> when the registry refused any step.
    /// </summary>
    /// <param name="registry">The PuTTY session store.</param>
    /// <param name="columns">Initial terminal width, in columns.</param>
    /// <param name="rows">Initial terminal height, in rows.</param>
    /// <param name="warn">Receives the warning when the session cannot be created.</param>
    internal static string? TryCreate(
        IPuttySessionRegistry registry,
        int columns,
        int rows,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        warn ??= FileLogger.Warn;

        string sessionName = PlinkSizeSessionNaming.CreateName();
        try
        {
            IReadOnlyList<PuttyRegistryValue> defaults =
                registry.ReadSession(PlinkSizeSessionNaming.DefaultSettingsSessionName) ?? [];

            List<PuttyRegistryValue> values = defaults
                .Where(static value => !IsOverriddenOrForbidden(value.Name))
                .ToList();
            values.Add(new PuttyRegistryValue(TermWidthValueName, columns, RegistryValueKind.DWord));
            values.Add(new PuttyRegistryValue(TermHeightValueName, rows, RegistryValueKind.DWord));

            registry.WriteSession(sessionName, values);

            // The name is logged: it is random, carries nothing about the user or the target, and
            // is what ties a leftover key found later to the launch that created it.
            FileLogger.Info(
                $"[{nameof(PlinkSizeSession)}] Created Plink size session {sessionName} carrying {columns}x{rows}");
            return sessionName;
        }
        catch (Exception ex)
        {
            warn(
                $"[{nameof(PlinkSizeSession)}] Could not create the Plink size session, launching at the default size: {ex.Message}");
            // A write that failed halfway may have left the key behind. Removing it now is best
            // effort; the startup janitor is the backstop when this fails too.
            Delete(registry, sessionName, warn);
            return null;
        }
    }

    /// <summary>
    /// Deletes a size session, logging rather than throwing: the connection must not fail because
    /// a leftover could not be removed, and the startup janitor collects it later.
    /// </summary>
    internal static void Delete(IPuttySessionRegistry registry, string sessionName, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        warn ??= FileLogger.Warn;

        try
        {
            registry.DeleteSession(sessionName);
        }
        catch (Exception ex)
        {
            warn($"[{nameof(PlinkSizeSession)}] Could not delete the Plink size session {sessionName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Arms the deletion of <paramref name="sessionName"/> and returns the handle every caller must
    /// go through. Call before starting the session so no output can be missed.
    /// </summary>
    /// <param name="session">The session that will run the launcher.</param>
    /// <param name="sessionName">The saved session to delete once it is safe to do so.</param>
    /// <param name="delete">Performs the deletion. Invoked at most once, from any path.</param>
    /// <param name="firstByteProvesConsumption">
    /// Whether the launcher's first byte of output proves it already parsed its command line, and
    /// so already loaded the session. Same attestation as the password file: <c>-load</c> is
    /// processed in the same command-line pass as <c>-pwfile</c>, before any network activity.
    /// </param>
    internal static PlinkSizeSessionReleaseHandle ArmRelease(
        ITerminalSession session,
        string sessionName,
        Action<string> delete,
        bool firstByteProvesConsumption)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);
        ArgumentNullException.ThrowIfNull(delete);

        return new PlinkSizeSessionReleaseHandle(session, sessionName, delete, firstByteProvesConsumption);
    }

    private static bool IsOverriddenOrForbidden(string valueName) =>
        string.Equals(valueName, HostNameValueName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(valueName, TermWidthValueName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(valueName, TermHeightValueName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Owns one temporary size session and deletes it once, whichever path gets there first.
/// </summary>
/// <remarks>
/// The same single-path shape as <see cref="PlinkPasswordFileReleaseHandle"/>: first output (when
/// attested), process exit, launch failure and cancellation all arrive here, and the launch failure
/// and cancellation paths dispose the session, which can itself raise
/// <see cref="ITerminalSession.ProcessExited"/>, before releasing explicitly.
/// </remarks>
internal sealed class PlinkSizeSessionReleaseHandle
{
    private readonly ITerminalSession _session;
    private readonly string _sessionName;
    private readonly Action<string> _delete;
    private readonly Action<ReadOnlyMemory<byte>>? _onData;
    private readonly Action<int> _onExit;

    private int _released;

    internal PlinkSizeSessionReleaseHandle(
        ITerminalSession session,
        string sessionName,
        Action<string> delete,
        bool firstByteProvesConsumption)
    {
        _session = session;
        _sessionName = sessionName;
        _delete = delete;

        _onExit = _ => Release();
        _session.ProcessExited += _onExit;

        if (!firstByteProvesConsumption)
        {
            return;
        }

        _onData = _ => Release();
        _session.DataReceived += _onData;
    }

    /// <summary>
    /// Deletes the size session if it has not been deleted yet, and stops listening.
    /// </summary>
    internal void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (_onData is not null)
        {
            _session.DataReceived -= _onData;
        }

        _session.ProcessExited -= _onExit;

        _delete(_sessionName);
    }
}
