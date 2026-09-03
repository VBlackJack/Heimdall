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

using System.Diagnostics;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Sftp;
using Heimdall.Ssh;

namespace Heimdall.App.Services;

/// <summary>
/// Immutable result of a connection attempt.
/// </summary>
/// <param name="Success">Whether the connection was established.</param>
/// <param name="ErrorMessage">Error description on failure; null on success.</param>
/// <param name="Session">Typed session result on success; null on failure.</param>
/// <param name="Failure">Optional structured failure details when the connection fails.</param>
/// <param name="Warning">Optional non-fatal warning to display on a successful connection.</param>
public sealed record ConnectionResult(
    bool Success,
    string? ErrorMessage,
    ISessionResult? Session,
    SessionDiagnostic? Failure = null,
    string? Warning = null);

/// <summary>Wraps a <see cref="ServerProfileDto"/> for embedded RDP sessions.</summary>
/// <param name="Server">The profile the session runs on.</param>
/// <param name="TunnelPort">Local end of the SSH tunnel, or null for a direct connection.</param>
/// <param name="ConnectionSettings">
/// The settings instance this connection was made with, or null when nothing recorded it.
/// </param>
/// <remarks>
/// <para><b>Why the settings travel with the result.</b> The pane is built from the profile
/// snapshot the connection used, but it used to read the gateway list from whatever settings the
/// application held at the moment the pane was materialised. Those are two different instants,
/// and settings are read as a fresh deep clone each time
/// <c>ConfigManager.CurrentSettings</c> is asked, so the second instant genuinely carries edits
/// the first one never saw. Editing a gateway's host during a slow tunnel establishment then
/// named the new host in the certificate question while the certificate had come from the old
/// one - and the question's route line exists precisely to tell two machines apart.</para>
/// <para><b>What this instance is.</b> The one <c>ITunnelService.SetupTunnelIfNeededAsync</c> was
/// handed, which is the one <c>TunnelService.EstablishTunnelAsync</c> resolved the gateway chain
/// from. Not a copy of it and not a re-read: the same object, so what is read back here is what
/// the chain was resolved from.</para>
/// <para><b>What it is still not.</b> A record of the route the connection travelled. A tunnel
/// that was already open is reused when its chain hashes the same, and that hash is over gateway
/// identifiers, which an edit leaves alone - so a reused tunnel can have been opened from an
/// older settings instance than this one. Recording the resolved chain on the tunnel is what
/// would settle that; this carrier narrows the disagreement to a reused tunnel rather than
/// removing it, and nothing written from it may claim to describe the wire.</para>
/// </remarks>
public sealed record RdpSessionResult(
    ServerProfileDto Server,
    int? TunnelPort = null,
    AppSettings? ConnectionSettings = null) : ISessionResult;

/// <summary>Wraps an SSH.NET shell session.</summary>
public sealed record SshSessionResult(
    SshShellSession Session,
    bool? SessionLoggingOverride = null) : ISessionResult;

/// <summary>Wraps a terminal session (Plink pipe mode, Telnet, or ConPTY).</summary>
public sealed record TerminalSessionResult(
    Heimdall.Terminal.ITerminalSession Session,
    string? Endpoint = null,
    bool? SessionLoggingOverride = null) : ISessionResult;

/// <summary>
/// Bundles an SFTP browser session with the SSH connection parameters needed for sudo operations.
/// </summary>
public sealed record SftpSessionBundle(
    SftpBrowser Browser,
    SshConnectionParams SshParams,
    bool? SessionLoggingOverride = null) : ISessionResult;

/// <summary>
/// Bundles a local shell terminal session with the resolved working directory.
/// </summary>
public sealed record LocalShellBundle(
    Heimdall.Terminal.ITerminalSession? Session,
    string WorkingDirectory,
    string ShellExecutable,
    bool IsElevated = false,
    bool? SessionLoggingOverride = null,
    int? ExternalProcessId = null) : ISessionResult
{
    /// <summary>True when the shell was launched in a separate elevated window.</summary>
    public bool IsExternal => Session is null && ExternalProcessId is not null;
}

/// <summary>
/// Holds VNC connection parameters for the embedded noVNC view.
/// The proxy and WebView2 rendering are managed by <see cref="Views.EmbeddedVncView"/>.
/// </summary>
public sealed record VncSessionResult(
    string ServerId,
    string Host,
    int Port,
    string? Password = null,
    bool ViewOnly = false,
    bool? SessionLoggingOverride = null) : ISessionResult;

/// <summary>
/// Bundles an FTP browser session for use by the embedded SFTP/FTP view.
/// </summary>
public sealed record FtpSessionBundle(
    FtpBrowser Browser,
    bool? SessionLoggingOverride = null) : ISessionResult;

/// <summary>
/// Describes which Citrix launch path was selected for the current session.
/// </summary>
public enum CitrixLaunchMode
{
    Unknown = 0,
    SelfServiceCache,
    IcaFile,
    StoreFront
}

/// <summary>
/// Wraps a Citrix Workspace process handle for session lifecycle management.
/// </summary>
/// <param name="PreLaunchWindows">
/// Visible top-level windows captured immediately BEFORE the launcher process was started. The
/// view uses it to tell the new session window from a pre-existing one; taking it after the launch
/// would race single sign-on and a warm cache, which can surface the session window first.
/// </param>
public sealed record CitrixSessionResult(
    Process? Process,
    string? StoreFrontUrl = null,
    string? AppName = null,
    CitrixLaunchMode Mode = CitrixLaunchMode.Unknown,
    bool? SessionLoggingOverride = null,
    IReadOnlySet<nint>? PreLaunchWindows = null) : ISessionResult;
