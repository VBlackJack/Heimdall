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
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.Ssh.Plink;

/// <summary>
/// Result of a plink tunnel establishment attempt.
/// </summary>
/// <param name="Success">Whether the tunnel was established and is forwarding traffic.</param>
/// <param name="ErrorMessage">Error description on failure; null on success.</param>
/// <param name="FailureCode">Structured failure code on failure; null on success.</param>
public sealed record PlinkTunnelResult(bool Success, string? ErrorMessage, SshFailureCode? FailureCode);

/// <summary>
/// Fallback tunnel implementation using an external plink.exe process.
/// Used when SSH.NET cannot handle the authentication method, such as
/// PuTTY/Pageant-specific agent flows where plink.exe can use the key
/// through its own compatible agent integration.
/// </summary>
/// <remarks>
/// This is a temporary bridge for Plink-specific fallback paths.
/// The legacy Heimdall (PowerShell) used plink exclusively for all tunnels.
/// </remarks>
public sealed class PlinkTunnelRunner : IDisposable
{
    private static readonly int PortCheckMaxAttempts = 15;
    private readonly TimeSpan _portCheckInterval;
    private readonly TimeSpan _processKillGracePeriod;

    private Process? _process;
    private string? _pwFilePath;
    private Task? _drainTask;
    private CancellationTokenSource? _drainCts;
    private bool _disposed;

    /// <summary>
    /// How long <see cref="Stop"/> waits for the stderr drain task to finish
    /// before forcibly killing the process. Kept conservative to avoid
    /// blocking application shutdown on a stuck pipe read.
    /// </summary>
    private static readonly TimeSpan DrainJoinTimeout = TimeSpan.FromMilliseconds(500);

    public PlinkTunnelRunner(
        int portCheckIntervalMs = AppSettings.DefaultPlinkPortCheckIntervalMs,
        int killGracePeriodMs = AppSettings.DefaultPlinkKillGracePeriodMs)
        : this(new PlinkTunnelRunnerOptions(portCheckIntervalMs, killGracePeriodMs))
    {
    }

    public PlinkTunnelRunner(PlinkTunnelRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _portCheckInterval = TimeSpan.FromMilliseconds(options.PortCheckIntervalMs);
        _processKillGracePeriod = TimeSpan.FromMilliseconds(options.KillGracePeriodMs);
    }

    /// <summary>Whether the underlying plink process is running.</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Process ID of the plink tunnel process, or null if not running.</summary>
    public int? ProcessId => IsRunning ? _process!.Id : null;

    /// <summary>
    /// Starts a plink.exe process that establishes an SSH tunnel
    /// with local port forwarding to the specified remote endpoint.
    /// </summary>
    /// <param name="plinkPath">Absolute path to plink.exe.</param>
    /// <param name="gatewayHost">SSH gateway hostname or IP.</param>
    /// <param name="gatewayPort">SSH gateway port.</param>
    /// <param name="username">SSH username.</param>
    /// <param name="keyPath">Path to private key file (PPK format). Optional.</param>
    /// <param name="password">SSH password. Optional. Written to a temporary file for -pwfile.</param>
    /// <param name="remoteHost">Target host on the remote network.</param>
    /// <param name="remotePort">Target port on the remote network.</param>
    /// <param name="localPort">Local port to bind for forwarding.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    /// <returns>Result indicating success or structured failure.</returns>
    public async Task<PlinkTunnelResult> StartAsync(
        string plinkPath,
        string gatewayHost,
        int gatewayPort,
        string username,
        string? keyPath,
        string? password,
        string remoteHost,
        int remotePort,
        int localPort,
        string? hostKeyFingerprint = null,
        CancellationToken cancellationToken = default,
        string? keyPassphrase = null,
        string? passphraseUnsupportedMessage = null,
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);

        if (_process is not null)
        {
            throw new InvalidOperationException("A plink tunnel is already running. Call Stop() first.");
        }

        if (!string.IsNullOrWhiteSpace(keyPath) && !string.IsNullOrEmpty(keyPassphrase))
        {
            return new PlinkTunnelResult(
                false,
                passphraseUnsupportedMessage
                    ?? "Plink fallback cannot unlock a passphrase-protected key file. Load the key in Pageant instead.",
                SshFailureCode.PassphraseRequired);
        }

        if (!File.Exists(plinkPath))
        {
            return new PlinkTunnelResult(false, $"Plink executable not found: {plinkPath}", SshFailureCode.Unknown);
        }

        List<string> args;
        ProcessStartInfo startInfo;
        try
        {
            // Build argument list
            args = BuildArguments(gatewayHost, gatewayPort, username, keyPath, password,
                remoteHost, remotePort, localPort, hostKeyFingerprint, localBindHost);
            startInfo = CreateStartInfo(plinkPath, args);
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or ArgumentOutOfRangeException)
        {
            return new PlinkTunnelResult(false, ex.Message, SshFailureCode.Unknown);
        }

        try
        {
            Process process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process = process;
            process.Exited += (_, _) => LogProcessExit(process, localPort);
            process.Start();
        }
        catch (Exception ex)
        {
            // Ensure password file is cleaned up if process fails to start
            CleanupPasswordFile();
            return new PlinkTunnelResult(false, $"Failed to start plink process: {ex.Message}", SshFailureCode.Unknown);
        }

        // Continuously drain stderr in the background to prevent buffer saturation.
        // The drain is owned by an internal CTS so Stop() can both cancel and
        // synchronously join it, eliminating "fire and forget" thread-pool
        // exceptions when the process is killed before the pipe drains.
        _drainCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var drainToken = _drainCts.Token;
        _drainTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var proc = _process;
                    if (proc is null || proc.HasExited || proc.StandardError is null)
                    {
                        break;
                    }

                    drainToken.ThrowIfCancellationRequested();
                    var line = await proc.StandardError.ReadLineAsync(drainToken).ConfigureAwait(false);
                    if (line is null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Core.Logging.FileLogger.Info($"Plink stderr (port {localPort}, untrusted): {SanitizeForLog(line)}");
                    }
                }
            }
            catch (OperationCanceledException) { /* Clean shutdown */ }
            catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[PlinkTunnelRunner] stderr drain: {ex.Message}"); }
        }, drainToken);

        try
        {
            // Wait for the local port to become reachable (tunnel established)
            bool portReady = await WaitForPortBindAsync(localPort, localBindHost, cancellationToken).ConfigureAwait(false);

            if (!portReady)
            {
                // Process may have already exited with an error
                var exitInfo = _process is { HasExited: true }
                    ? $"(exit code {_process.ExitCode})"
                    : "(still running but port not bound)";
                Stop();

                var message = $"Plink tunnel failed to bind port {localPort} within timeout {exitInfo}";
                Core.Logging.FileLogger.Error(message);
                return new PlinkTunnelResult(false, message, SshFailureCode.Unknown);
            }

            // Plink consumes -pwfile during process startup. Once the local
            // forwarding port is bound, the plaintext file is no longer needed.
            CleanupPasswordFile();
            return new PlinkTunnelResult(true, null, null);
        }
        catch (OperationCanceledException)
        {
            Stop();
            return new PlinkTunnelResult(false, "Tunnel establishment was cancelled.", SshFailureCode.Cancelled);
        }
        catch (Exception ex)
        {
            Stop();
            return new PlinkTunnelResult(false, $"Failed to start plink process: {ex.Message}", SshFailureCode.Unknown);
        }
    }

    /// <summary>
    /// Logs that a plink tunnel process exited. Best-effort diagnostics: the
    /// <see cref="Process"/> may already have been disposed by a concurrent
    /// <see cref="Stop"/>, so reading its id is guarded.
    /// </summary>
    internal static void LogProcessExit(Process process, int localPort)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            Core.Logging.FileLogger.Warn(
                $"Plink tunnel process exited (pid={process.Id}, port={localPort})");
        }
        catch (Exception ex)
        {
            // The Process was disposed (or never started) by the time the
            // Exited callback ran on its thread-pool thread. The exit
            // notification is diagnostics-only; never let it escape.
            Core.Logging.FileLogger.Debug(
                $"[PlinkTunnelRunner] exit-log suppressed (port={localPort}): {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the plink tunnel process and cleans up temporary files.
    /// Cancels the stderr drain task and joins it (with a short timeout)
    /// before killing the process so background reads don't outlive the
    /// pipe they were attached to.
    /// </summary>
    public void Stop()
    {
        // Signal the drain to stop and wait briefly for it to release the pipe.
        try
        {
            _drainCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The CTS may have been disposed by an earlier Stop() / Dispose();
            // safe to ignore — the drain task is already gone.
        }

        if (_drainTask is not null)
        {
            try
            {
                _drainTask.Wait(DrainJoinTimeout);
            }
            catch (AggregateException)
            {
                // Drain failures are already logged inside the drain Task itself.
            }
            _drainTask = null;
        }

        _drainCts?.Dispose();
        _drainCts = null;

        if (_process is not null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit((int)_processKillGracePeriod.TotalMilliseconds);
                }
            }
            catch (InvalidOperationException ex)
            {
                Heimdall.Core.Logging.FileLogger.Warn($"[PlinkTunnelRunner] Stop: {ex.Message}");
            }

            _process.Dispose();
            _process = null;
        }

        CleanupPasswordFile();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    /// <summary>
    /// Builds the plink command-line argument list for tunnel mode.
    /// Uses -N (no shell), -ssh (force SSH), -L (local forwarding).
    /// Password is written to a temp file and passed via -pwfile to avoid
    /// exposing it on the command line.
    /// </summary>
    internal List<string> BuildArguments(
        string gatewayHost,
        int gatewayPort,
        string username,
        string? keyPath,
        string? password,
        string remoteHost,
        int remotePort,
        int localPort,
        string? hostKeyFingerprint = null,
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        ValidateConnectionInputs(gatewayHost, gatewayPort, username, keyPath, remoteHost, remotePort, localPort, localBindHost);
        localBindHost = LoopbackBinding.NormalizeHost(localBindHost);

        var args = new List<string>
        {
            "-ssh",
            "-batch", // non-interactive: fail instead of prompting
            "-N", // no shell, tunnel only
            "-L", BuildLocalForwardArgument(localBindHost, localPort, remoteHost, remotePort),
            "-P", gatewayPort.ToString()
        };

        // Use TOFU host key fingerprint if available to prevent interactive prompts
        if (!string.IsNullOrEmpty(hostKeyFingerprint))
        {
            args.Add("-hostkey");
            args.Add(hostKeyFingerprint);
        }
        else
        {
            Heimdall.Core.Logging.FileLogger.Warn(
                $"PlinkTunnelRunner: launching without -hostkey for {gatewayHost}:{gatewayPort}. " +
                "This should never happen in production paths after the fail-closed refactor.");
        }

        if (!string.IsNullOrEmpty(keyPath))
        {
            args.Add("-i");
            args.Add(keyPath);
        }

        if (!string.IsNullOrEmpty(password))
        {
            // Write password to a temporary file and use -pwfile.
            // This avoids exposing the password on the command line.
            // Create the file with restricted ACL atomically to eliminate
            // the TOCTOU window between creation and permission enforcement.
            CleanupPasswordFile();
            _pwFilePath = Path.Combine(
                Path.GetTempPath(),
                $"{PlinkPasswordFileNaming.Prefix}{Guid.NewGuid():N}");

            if (OperatingSystem.IsWindows())
            {
                Heimdall.Core.Security.SecureFileWriter.WriteAndProtect(_pwFilePath, password);
            }
            else
            {
                File.WriteAllText(_pwFilePath, password);
                // Best-effort POSIX permission restriction (mode 0600)
                try
                {
                    File.SetUnixFileMode(_pwFilePath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (Exception ex)
                {
                    Heimdall.Core.Logging.FileLogger.Warn(
                        $"[PlinkTunnelRunner] Unix file mode restriction failed: {ex.Message}");
                }
            }

            args.Add("-pwfile");
            args.Add(_pwFilePath);
        }

        args.Add($"{username}@{gatewayHost}");

        return args;
    }

    private static string BuildLocalForwardArgument(
        string localBindHost,
        int localPort,
        string remoteHost,
        int remotePort)
    {
        return LoopbackBinding.IsDefaultHost(localBindHost)
            ? $"{localPort}:{remoteHost}:{remotePort}"
            : $"{localBindHost}:{localPort}:{remoteHost}:{remotePort}";
    }

    internal static void ValidateConnectionInputs(
        string gatewayHost,
        int gatewayPort,
        string username,
        string? keyPath,
        string remoteHost,
        int remotePort,
        int localPort,
        string localBindHost = LoopbackBinding.DefaultHost)
    {
        if (!IsValidHost(gatewayHost))
        {
            throw new ArgumentException($"Invalid gateway host: {gatewayHost}", nameof(gatewayHost));
        }

        if (!InputValidator.Validate(username, "SshUser"))
        {
            throw new ArgumentException($"Invalid SSH username: {username}", nameof(username));
        }

        if (!IsValidHost(remoteHost))
        {
            throw new ArgumentException($"Invalid remote host: {remoteHost}", nameof(remoteHost));
        }

        if (!InputValidator.ValidatePortRange(gatewayPort))
        {
            throw new ArgumentOutOfRangeException(nameof(gatewayPort));
        }

        if (!InputValidator.ValidatePortRange(remotePort))
        {
            throw new ArgumentOutOfRangeException(nameof(remotePort));
        }

        if (!InputValidator.ValidatePortRange(localPort))
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        LoopbackBinding.NormalizeHost(localBindHost);
        ValidateKeyPath(keyPath);
    }

    internal static void ValidateKeyPath(string? keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        if (keyPath.Contains('\0') || keyPath.Contains('"'))
        {
            throw new ArgumentException($"Invalid SSH key path: {keyPath}", nameof(keyPath));
        }

        if (!Path.IsPathRooted(keyPath))
        {
            throw new ArgumentException($"SSH key path must be absolute: {keyPath}", nameof(keyPath));
        }

        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException($"SSH key file not found: {keyPath}", keyPath);
        }
    }

    internal ProcessStartInfo CreateStartInfo(string plinkPath, IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plinkPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            RedirectStandardOutput = false
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    /// <summary>
    /// Match credential-like assignments where the secret is a single token
    /// (<c>password=...</c>, <c>passphrase: ...</c>, <c>secret=...</c>).
    /// </summary>
    private static readonly Regex SingleTokenCredentialPattern = new(
        @"(?i)\b(password|passphrase|secret)\b\s*[:=]?\s*\S+",
        RegexOptions.Compiled);

    /// <summary>
    /// Match credential-like assignments where the secret can span multiple
    /// tokens (<c>token ...</c>, <c>Authorization: Bearer ...</c>). Greedy
    /// to end-of-line so trailing words are not leaked.
    /// </summary>
    private static readonly Regex EndOfLineCredentialPattern = new(
        @"(?i)\b(token|bearer)\b\s*[:=]?\s*.+",
        RegexOptions.Compiled);

    /// <summary>
    /// Match Plink-style credential CLI flags (<c>-pw</c>, <c>-pwfile</c>) and
    /// the value that follows.
    /// </summary>
    private static readonly Regex PlinkCredentialFlagPattern = new(
        @"(?i)-pw(?:file)?\s+\S+",
        RegexOptions.Compiled);

    private const string RedactedMarker = "[REDACTED]";

    internal static string SanitizeForLog(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            // Replace non-tab control characters to preserve log readability and structure.
            if ((c < 32 && c != '\t') || c == 127)
            {
                builder.Append('?');
            }
            else
            {
                builder.Append(c);
            }
        }

        // Redact known secret-bearing patterns. Done after the control-char
        // pass so attackers cannot smuggle a regex break via embedded \0 etc.
        var redacted = PlinkCredentialFlagPattern.Replace(builder.ToString(), RedactedMarker);
        redacted = EndOfLineCredentialPattern.Replace(redacted, RedactedMarker);
        redacted = SingleTokenCredentialPattern.Replace(redacted, RedactedMarker);

        const int maxLength = 256;
        if (redacted.Length <= maxLength)
        {
            return redacted;
        }

        return $"{redacted[..maxLength]} [...]";
    }

    private static bool IsValidHost(string host)
    {
        return !string.IsNullOrWhiteSpace(host)
            && (InputValidator.ValidateDomain(host) || IPAddress.TryParse(host, out _));
    }

    /// <summary>
    /// Waits for the local forwarded port to become reachable, indicating
    /// the tunnel is established and forwarding traffic.
    /// Uses a retry loop with configurable attempts and interval.
    /// </summary>
    private async Task<bool> WaitForPortBindAsync(
        int localPort,
        string localBindHost,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < PortCheckMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await Task.Delay(_portCheckInterval, cancellationToken).ConfigureAwait(false);

            if (await IsPortListeningAsync(localBindHost, localPort).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a TCP port is listening on localhost by attempting a brief connection.
    /// </summary>
    private static async Task<bool> IsPortListeningAsync(string localBindHost, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(localBindHost, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);
            return completed == connectTask && client.Connected;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the temporary password file if one was created.
    /// </summary>
    private void CleanupPasswordFile()
    {
        string? passwordFilePath = _pwFilePath;
        if (passwordFilePath is null)
        {
            return;
        }

        try
        {
            File.Delete(passwordFilePath);
            _pwFilePath = null;
        }
        catch (IOException ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[PlinkTunnelRunner] CleanupPasswordFile: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Heimdall.Core.Logging.FileLogger.Warn($"[PlinkTunnelRunner] CleanupPasswordFile: {ex.Message}");
        }
    }
}
