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

namespace Heimdall.Core.Security;

/// <summary>
/// Credential provider that executes an external CLI command to retrieve passwords.
/// Works with any CLI-based password manager: KeePassXC, Bitwarden CLI, 1Password CLI, pass, etc.
/// </summary>
/// <remarks>
/// The command template supports the following placeholders:
/// <list type="bullet">
///   <item><c>{Host}</c> — Target server hostname or IP</item>
///   <item><c>{Port}</c> — Target port number</item>
///   <item><c>{User}</c> — Username hint</item>
///   <item><c>{Title}</c> — Server display name / entry title</item>
///   <item><c>{Database}</c> — Configured database path</item>
///   <item><c>{KeyFile}</c> - Path to the key file (replaces <c>{KeyFile}</c>)</item>
/// </list>
/// The command's stdout is captured and trimmed as the password.
/// </remarks>
public sealed class CommandCredentialProvider : ICredentialProvider
{
    private readonly TimeSpan _commandTimeout;

    private readonly string? _commandTemplate;
    private readonly string? _databasePath;
    private readonly string? _keyFilePath;
    private readonly string? _unlockSecret;
    private readonly string? _usernameCommandTemplate;
    private readonly bool _firstLineOnly;

    /// <summary>
    /// Initializes the provider with the command template and optional database path.
    /// </summary>
    /// <param name="commandTemplate">
    /// The full command with placeholders. Example:
    /// <c>keepassxc-cli show -s -a Password "{Database}" "{Title}"</c>
    /// </param>
    /// <param name="databasePath">
    /// Path to the password database file (replaces <c>{Database}</c>).
    /// </param>
    /// <param name="timeoutMs">Command execution timeout in milliseconds.</param>
    /// <param name="unlockSecret">
    /// Optional secret (database master password / GPG passphrase) written to the
    /// child process stdin followed by a newline. Required by tools such as
    /// <c>keepassxc-cli</c> and <c>pass</c> that prompt for an unlock secret. When
    /// null or empty, stdin is not redirected (the tool runs non-interactively).
    /// </param>
    /// <param name="usernameCommandTemplate">
    /// Optional second command (same placeholders, same timeout and unlock-secret
    /// stdin) that retrieves the username from the vault. Run only when the caller
    /// passes no username hint. A failed or empty username command never fails the
    /// overall call: the username hint is used instead.
    /// </param>
    /// <param name="firstLineOnly">
    /// When true, the command result is the first non-empty (trimmed) line of stdout
    /// instead of the whole trimmed output. Required by tools that print the value on
    /// line 1 followed by status text (e.g. KeePass2 <c>KPScript</c>'s trailing
    /// <c>OK: ...</c> line, or <c>pass</c>'s notes after the password). Applies to both
    /// the password and username commands.
    /// </param>
    /// <param name="keyFilePath">
    /// Optional path to a key file (replaces <c>{KeyFile}</c>). Used by tools such as
    /// <c>keepassxc-cli</c> that authenticate with a key file via <c>-k/--key-file</c>,
    /// alone or combined with a master password.
    /// </param>
    public CommandCredentialProvider(
        string? commandTemplate,
        string? databasePath,
        int timeoutMs = 10000,
        string? unlockSecret = null,
        string? usernameCommandTemplate = null,
        bool firstLineOnly = false,
        string? keyFilePath = null)
    {
        _commandTemplate = commandTemplate;
        _databasePath = databasePath;
        _commandTimeout = TimeSpan.FromMilliseconds(timeoutMs);
        _unlockSecret = unlockSecret;
        _usernameCommandTemplate = usernameCommandTemplate;
        _firstLineOnly = firstLineOnly;
        _keyFilePath = keyFilePath;
    }

    /// <inheritdoc />
    public string Name => "Command";

    /// <inheritdoc />
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_commandTemplate);

    /// <inheritdoc />
    public async Task<CredentialResult?> GetCredentialAsync(
        string serverHost,
        int port,
        string? username,
        string? title,
        CancellationToken ct = default)
    {
        if (!IsAvailable || _commandTemplate is null)
        {
            return null;
        }

        string? password;
        try
        {
            password = await RunCommandAsync(_commandTemplate, serverHost, port, username, title, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logging.FileLogger.Warn("CommandCredentialProvider: command timed out");
            throw;
        }

        if (string.IsNullOrEmpty(password))
        {
            // RunCommandAsync already logged the reason (empty output, non-zero exit, or failure).
            return null;
        }

        string resolvedUsername = await ResolveUsernameAsync(
            serverHost, port, username, title, ct).ConfigureAwait(false);

        return new CredentialResult(resolvedUsername, password);
    }

    /// <summary>
    /// Resolves the username for the returned credential. When a username command is
    /// configured and the caller supplied no username hint, the command's stdout is used;
    /// otherwise (and on any username-command failure/empty output) the hint is echoed.
    /// A failed username command never fails the overall credential lookup.
    /// </summary>
    private async Task<string> ResolveUsernameAsync(
        string serverHost,
        int port,
        string? username,
        string? title,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_usernameCommandTemplate)
            || !string.IsNullOrWhiteSpace(username))
        {
            return username ?? string.Empty;
        }

        try
        {
            string? resolved = await RunCommandAsync(
                _usernameCommandTemplate, serverHost, port, username, title, ct)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            // Empty/failed username command (already logged by RunCommandAsync): fall back to the hint.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The username command hit its own timeout (the caller's token is not cancelled).
            // This must not fail the whole call: fall back to the hint. Never log the value.
            Logging.FileLogger.Warn("CommandCredentialProvider: username command timed out");
        }

        return username ?? string.Empty;
    }

    /// <summary>
    /// Executes a single credential command and returns its trimmed stdout, or null when the
    /// command produced empty output, exited non-zero, or failed to launch. Applies the configured
    /// timeout and unlock-secret stdin injection, drains stderr, and kills a hung process on
    /// timeout/cancellation. Throws <see cref="OperationCanceledException"/> on timeout/cancellation.
    /// </summary>
    private async Task<string?> RunCommandAsync(
        string template,
        string serverHost,
        int port,
        string? username,
        string? title,
        CancellationToken ct)
    {
        var expandedCommand = ExpandTemplate(template, serverHost, port, username, title);
        var (executable, arguments) = SplitCommand(expandedCommand);

        Logging.FileLogger.Info($"CommandCredentialProvider: executing command for host={serverHost}");

        // When set, the unlock secret is fed to the child process stdin so tools that
        // prompt for a database/GPG passphrase can run unattended.
        bool injectUnlockSecret = !string.IsNullOrEmpty(_unlockSecret);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = injectUnlockSecret
            };

            process.Start();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(_commandTimeout);

            // Drain stderr concurrently to prevent pipe buffer deadlock (4 KB limit on Windows).
            // Not logged - external credential tools may echo credential fragments to stderr.
            Task<string> stderrDrain = process.StandardError.ReadToEndAsync(linkedCts.Token);

            string stdout;
            try
            {
                if (injectUnlockSecret)
                {
                    // Write the unlock secret followed by a newline, then close the stream so the
                    // tool receives EOF. Shares the linked timeout/cancellation; a cancellation here
                    // is handled by the catch below, which kills the process. Never logged.
                    try
                    {
                        await process.StandardInput.WriteLineAsync(
                            (_unlockSecret ?? string.Empty).AsMemory(), linkedCts.Token)
                            .ConfigureAwait(false);
                        await process.StandardInput.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        process.StandardInput.Close();
                    }
                }

                stdout = await process.StandardOutput.ReadToEndAsync(linkedCts.Token)
                    .ConfigureAwait(false);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Any timeout/cancellation - during the stdin write, stdout read, OR the exit wait - must
                // kill the external process before `using` disposes it (Dispose does not terminate the
                // process), otherwise a hung credential tool is left orphaned holding its database lock.
                TryKillProcess(process);
                throw;
            }
            finally
            {
                // Observe the stderr drain so a cancelled/closed pipe never surfaces as an unobserved task
                // exception. Its content is intentionally discarded (it may contain credential fragments).
                await ObserveSilentlyAsync(stderrDrain).ConfigureAwait(false);
            }

            if (process.ExitCode != 0)
            {
                // Log only exit code — stderr may contain credential fragments from
                // the external tool and must not be persisted to log files.
                Logging.FileLogger.Warn(
                    $"CommandCredentialProvider: command exited with code {process.ExitCode}");
                return null;
            }

            string output = _firstLineOnly ? FirstNonEmptyLine(stdout) : stdout.Trim();
            if (string.IsNullOrEmpty(output))
            {
                Logging.FileLogger.Warn("CommandCredentialProvider: command returned empty output");
                return null;
            }

            return output;
        }
        catch (OperationCanceledException)
        {
            // Propagate to the caller, which decides whether a timeout fails the whole lookup
            // (password command) or falls back to the hint (username command).
            throw;
        }
        catch (Exception ex)
        {
            Logging.FileLogger.Warn($"CommandCredentialProvider: failed to execute command: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Replaces placeholders in the command template with actual values.
    /// Sanitization is context-aware: the executable is extracted from the
    /// raw template to determine whether a shell interpreter is the target.
    /// Shell targets (cmd.exe, .bat, .cmd, PowerShell, WSL, WSH) get strict
    /// stripping; regular executables (keepassxc-cli, bw, op) get relaxed
    /// stripping that preserves parentheses, single quotes, and percent signs
    /// in legitimate values (double quotes are always stripped).
    /// The path placeholders (<c>{Database}</c>, <c>{KeyFile}</c>) use a path-aware
    /// sanitizer for non-shell targets so legitimate path characters survive.
    /// </summary>
    internal string ExpandTemplate(
        string template,
        string host,
        int port,
        string? username,
        string? title)
    {
        var (executable, _) = SplitCommand(template);
        bool shellTarget = InputValidator.IsShellTarget(executable);
        Func<string?, string> sanitize = shellTarget ? SanitizeStrict : SanitizeRelaxed;
        Func<string?, string> sanitizePath = shellTarget ? SanitizeStrict : SanitizePathValue;

        return template
            .Replace("{Host}", sanitize(host), StringComparison.OrdinalIgnoreCase)
            .Replace("{Port}", port.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{User}", sanitize(username), StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", sanitize(title), StringComparison.OrdinalIgnoreCase)
            .Replace("{Database}", sanitizePath(_databasePath), StringComparison.OrdinalIgnoreCase)
            .Replace("{KeyFile}", sanitizePath(_keyFilePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips all shell metacharacters — used when the template targets a shell
    /// interpreter (cmd.exe, .bat, .cmd, PowerShell) or when unknown.
    /// </summary>
    private static string SanitizeStrict(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(value, @"[;&|`$<>()!""'\r\n%^]", "");
    }

    /// <summary>
    /// Strips only characters that affect MSVC CRT argument parsing (double
    /// quotes) or could chain/redirect process execution. Preserves
    /// parentheses, single quotes, percent signs, etc. Double quotes are
    /// still stripped as they control argument boundary parsing in all
    /// Windows executables. Used when the template targets a regular executable.
    /// </summary>
    private static string SanitizeRelaxed(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(value, @"[;&|`$<>""\r\n]", "");
    }

    /// <summary>
    /// Sanitizes a filesystem path placeholder (<c>{Database}</c>, <c>{KeyFile}</c>) for
    /// non-shell targets. The process runs with <c>UseShellExecute=false</c>, so no shell
    /// interprets the arguments; the only argument-boundary metacharacter is the double
    /// quote (itself illegal in Windows filenames). Stripping only " / CR / LF preserves
    /// every legal path character (&amp; $ ( ) ' space) that SanitizeRelaxed wrongly removes.
    /// </summary>
    private static string SanitizePathValue(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return System.Text.RegularExpressions.Regex.Replace(value, "[\"\\r\\n]", "");
    }

    /// <summary>
    /// Splits a command line into executable and arguments, respecting quoted paths.
    /// </summary>
    private static (string Executable, string Arguments) SplitCommand(string commandLine)
    {
        var trimmed = commandLine.Trim();

        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote > 0)
            {
                var exe = trimmed[1..endQuote];
                var args = endQuote + 1 < trimmed.Length
                    ? trimmed[(endQuote + 1)..].TrimStart()
                    : string.Empty;
                return (exe, args);
            }
        }

        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex < 0)
        {
            return (trimmed, string.Empty);
        }

        return (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..]);
    }

    /// <summary>
    /// Returns the first non-empty, trimmed line of the supplied text, or an empty string
    /// when every line is blank. Used by the "first line only" output mode.
    /// </summary>
    private static string FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Attempts to kill a process that has timed out.
    /// </summary>
    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Awaits a background task and swallows any exception, so a cancelled or closed pipe never
    /// surfaces as an unobserved task exception. Used to observe the stderr drain.
    /// </summary>
    private static async Task ObserveSilentlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the stderr drain may be cancelled or the pipe closed when the process is killed.
        }
    }
}
