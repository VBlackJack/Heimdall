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

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Heimdall.Core.Security;

/// <summary>
/// Credential provider that executes an external CLI command to retrieve passwords.
/// Works with any CLI-based password manager: KeePassXC, Bitwarden CLI, 1Password CLI, pass, etc.
/// </summary>
/// <remarks>
/// The command template supports the following placeholders:
/// <list type="bullet">
///   <item><c>{Host}</c> - Target server hostname or IP</item>
///   <item><c>{Port}</c> - Target port number</item>
///   <item><c>{User}</c> - Username hint</item>
///   <item><c>{Title}</c> - Server display name / entry title</item>
///   <item><c>{Database}</c> - Configured database path</item>
///   <item><c>{KeyFile}</c> - Path to the key file (replaces <c>{KeyFile}</c>)</item>
/// </list>
/// The command's stdout is captured as the password, less its final line terminator.
/// </remarks>
public sealed class CommandCredentialProvider : ICredentialProvider
{
    /// <summary>
    /// The encoding of every pipe to the credential tool. Without it the pipes take the console
    /// code page, and Heimdall has no console: they fell back to the ANSI code page, so every
    /// accented letter of a password came back from keepassxc-cli as two wrong characters, and an
    /// unlock secret with an accent failed to open the database. keepassxc-cli, bw, op and gopass all
    /// speak UTF-8. No byte order mark: one at the head of stdin would become part of the secret.
    /// </summary>
    private static readonly Encoding PipeEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>UTF-8 that refuses invalid bytes, so that output in another code page is noticed.</summary>
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Password manager command-line tools that are known to parse their own arguments and never
    /// hand them to a shell. Their placeholder values keep every character a vault entry name can
    /// hold: an entry called "Srv R&amp;D" was looked up as "Srv RD" and never found. Any other
    /// non-shell executable keeps the relaxed stripping, since what it does with an argument is
    /// not known.
    /// </summary>
    private static readonly HashSet<string> KnownVaultTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "keepassxc-cli",
        "kpscript",
        "bw",
        "op",
        "pass",
        "gopass",
    };

    /// <summary>The prefixes of KPScript's status lines, printed after the value.</summary>
    private const string KpScriptOkPrefix = "OK: ";
    private const string KpScriptErrorPrefix = "E: ";

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
        catch (CredentialProviderTimeoutException)
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
                // A username has no meaningful edge whitespace, unlike a password.
                return resolved.Trim();
            }

            // Empty/failed username command (already logged by RunCommandAsync): fall back to the hint.
        }
        catch (CredentialProviderTimeoutException)
        {
            // The username command hit its own timeout. This must not fail the whole call: fall
            // back to the hint. Never log the value.
            Logging.FileLogger.Warn("CommandCredentialProvider: username command timed out");
        }
        catch (CredentialProviderLaunchException)
        {
            // Already logged by RunCommandAsync. The password was found, so the call stands.
        }

        return username ?? string.Empty;
    }

    /// <summary>
    /// Executes a single credential command and returns its stdout less the final line terminator,
    /// or null when the command produced blank output or exited non-zero. Applies the configured
    /// timeout and unlock-secret stdin injection, drains stderr, and kills a hung process tree on
    /// timeout/cancellation. Throws <see cref="CredentialProviderTimeoutException"/> when the
    /// configured timeout elapses, <see cref="OperationCanceledException"/> when the caller cancels,
    /// and <see cref="CredentialProviderLaunchException"/> when the executable cannot be started.
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
            process.StartInfo = BuildStartInfo(executable, arguments, injectUnlockSecret);

            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Logging.FileLogger.Warn(
                    $"CommandCredentialProvider: failed to start '{executable}': {ex.Message}");
                throw new CredentialProviderLaunchException(executable, ex);
            }

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

                stdout = await ReadOutputAsync(process.StandardOutput.BaseStream, linkedCts.Token)
                    .ConfigureAwait(false);
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Any timeout/cancellation - during the stdin write, stdout read, OR the exit wait - must
                // kill the external process before `using` disposes it (Dispose does not terminate the
                // process), otherwise a hung credential tool is left orphaned holding its database lock.
                TryKillProcess(process);

                if (!ct.IsCancellationRequested)
                {
                    // Only the linked timeout fired: the vault failed, the caller did not cancel.
                    throw new CredentialProviderTimeoutException(_commandTimeout);
                }

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
                // Log only exit code - stderr may contain credential fragments from
                // the external tool and must not be persisted to log files.
                Logging.FileLogger.Warn(
                    $"CommandCredentialProvider: command exited with code {process.ExitCode}");
                return null;
            }

            // Only the line breaks around the value are removed. A password may begin or end with
            // a space, and keepassxc-cli prints it so; trimming handed the server a different
            // password from the one in the vault.
            string output = _firstLineOnly ? FirstNonEmptyLine(stdout) : StripLineBreaks(stdout);
            if (string.IsNullOrWhiteSpace(output))
            {
                Logging.FileLogger.Warn("CommandCredentialProvider: command returned empty output");
                return null;
            }

            return output;
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or CredentialProviderTimeoutException
            or CredentialProviderLaunchException)
        {
            // Propagate to the caller, which decides whether the failure ends the whole lookup
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
    /// Reads the whole output and decodes it as UTF-8, or, when the bytes are not valid UTF-8,
    /// in the console's OEM code page.
    /// </summary>
    /// <remarks>
    /// keepassxc-cli, bw, op and gopass write UTF-8. A console program of the .NET Framework
    /// (KPScript), a .bat wrapper or cmd's own echo write in the OEM code page instead (850 or 437
    /// in Western Europe), where an accented letter is one byte that is never valid UTF-8 on its
    /// own. Decoded as UTF-8 it became a replacement character, and as ANSI before that another
    /// wrong letter. Valid UTF-8 from an OEM tool is ASCII, which reads the same either way.
    /// </remarks>
    internal static async Task<string> ReadOutputAsync(Stream output, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await output.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return DecodeOutput(buffer.ToArray());
    }

    /// <summary>Decodes tool output: strict UTF-8 first, the OEM code page as the fallback.</summary>
    internal static string DecodeOutput(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return OemEncoding().GetString(bytes);
        }
    }

    private static Encoding OemEncoding()
    {
        // The OEM code pages live in the code-pages provider on .NET; registering it again is
        // harmless, and the application registers it at startup anyway. The console code page of
        // a child is the system's OEM code page, not the one of the user's culture.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int codePage = OperatingSystem.IsWindows()
            ? (int)GetOEMCP()
            : System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
        return Encoding.GetEncoding(codePage);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// The start parameters of a credential command: no window, no shell, every pipe it uses
    /// redirected and in <see cref="PipeEncoding"/>.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(string executable, string arguments, bool redirectStandardInput)
    {
        return new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            StandardOutputEncoding = PipeEncoding,
            StandardErrorEncoding = PipeEncoding,
            StandardInputEncoding = redirectStandardInput ? PipeEncoding : null
        };
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
    /// sanitizer for non-shell targets so legitimate path characters survive, and so do every
    /// placeholder of a known password manager tool (see <see cref="KnownVaultTools"/>).
    /// For non-shell targets and cmd, a value that ends in backslashes right before a closing
    /// double quote has them doubled, so the quote still closes the argument (MSVC CRT rules).
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
        Func<string?, string> sanitizePath = shellTarget ? SanitizeStrict : SanitizePathValue;
        Func<string?, string> sanitize = shellTarget
            ? SanitizeStrict
            : IsKnownVaultTool(executable) ? SanitizePathValue : SanitizeRelaxed;

        var placeholders = new (string Token, string Value)[]
        {
            ("{Host}", sanitize(host)),
            ("{Port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("{User}", sanitize(username)),
            ("{Title}", sanitize(title)),
            ("{Database}", sanitizePath(_databasePath)),
            ("{KeyFile}", sanitizePath(_keyFilePath)),
        };

        // cmd hands a quoted argument to the program it starts unchanged, and that program parses
        // it by the CRT rules, so the closing quote needs the same protection there.
        bool protectClosingQuote = !shellTarget || IsCmdTarget(executable);
        return ExpandPlaceholders(template, placeholders, protectClosingQuote);
    }

    /// <summary>
    /// Replaces every placeholder of the template in one pass over the template only, so a value
    /// is never expanded again: a host or entry name that contains <c>{KeyFile}</c> used to be
    /// replaced by the local key file path. When an occurrence is followed by a double quote and
    /// <paramref name="protectClosingQuote"/> is set, trailing backslashes of the value are
    /// doubled: an entry named <c>share\</c> would otherwise turn the closing quote into a
    /// literal one and merge the following arguments into it.
    /// </summary>
    private static string ExpandPlaceholders(
        string template,
        IReadOnlyList<(string Token, string Value)> placeholders,
        bool protectClosingQuote)
    {
        var builder = new StringBuilder(template.Length);
        int position = 0;
        while (position < template.Length)
        {
            (string Token, string Value)? match = null;
            if (template[position] == '{')
            {
                foreach (var placeholder in placeholders)
                {
                    if (string.Compare(template, position, placeholder.Token, 0, placeholder.Token.Length,
                            StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        match = placeholder;
                        break;
                    }
                }
            }

            if (match is not { } found)
            {
                builder.Append(template[position]);
                position++;
                continue;
            }

            int after = position + found.Token.Length;
            bool closedByQuote = after < template.Length && template[after] == '"';
            builder.Append(protectClosingQuote && closedByQuote ? DoubleTrailingBackslashes(found.Value) : found.Value);
            position = after;
        }

        return builder.ToString();
    }

    /// <summary>Whether the executable is cmd.exe or a batch file that cmd runs.</summary>
    private static bool IsCmdTarget(string executable)
    {
        string name = Path.GetFileName(executable.Trim().TrimEnd('.', ' '));
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static string DoubleTrailingBackslashes(string value)
    {
        int trailing = 0;
        while (trailing < value.Length && value[value.Length - 1 - trailing] == '\\')
        {
            trailing++;
        }

        return trailing == 0 ? value : value + new string('\\', trailing);
    }

    /// <summary>Whether the executable is one of <see cref="KnownVaultTools"/>.</summary>
    private static bool IsKnownVaultTool(string executable)
    {
        string stem = Path.GetFileNameWithoutExtension(executable.Trim().TrimEnd('.', ' '));
        return KnownVaultTools.Contains(stem);
    }

    /// <summary>
    /// Strips all shell metacharacters - used when the template targets a shell
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
    /// Returns the first line of the supplied text that is not blank, without its line
    /// terminator, or an empty string when every line is blank. Used by the "first line only"
    /// output mode. Spaces inside the line are part of the value and are kept.
    /// </summary>
    /// <remarks>
    /// A first line that is KPScript's own status line is no value: KPScript prints its value,
    /// then "OK: ..." (or "E: ..." on an error), and for an entry whose field is empty the value
    /// line is blank, so skipping it handed the status line over as the password.
    /// </remarks>
    private static string FirstNonEmptyLine(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var value = line.TrimEnd('\r');
            if (!string.IsNullOrWhiteSpace(value))
            {
                return IsKpScriptStatusLine(value) ? string.Empty : value;
            }
        }

        return string.Empty;
    }

    private static bool IsKpScriptStatusLine(string line) =>
        line.StartsWith(KpScriptOkPrefix, StringComparison.Ordinal)
        || line.StartsWith(KpScriptErrorPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Removes the line breaks around the value, and only them: spaces and tabs at its edges are
    /// part of a password. Removing a single final terminator was too little, since a wrapper
    /// script that prints a blank line before or after the value then handed it over with the
    /// line break inside.
    /// </summary>
    private static string StripLineBreaks(string text) => text.Trim('\r', '\n');

    /// <summary>
    /// Attempts to kill a process that has timed out.
    /// </summary>
    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The whole tree: a template run through cmd /c or wsl leaves the vault tool as a
                // child, and killing only the parent left it holding the database lock.
                process.Kill(entireProcessTree: true);
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
