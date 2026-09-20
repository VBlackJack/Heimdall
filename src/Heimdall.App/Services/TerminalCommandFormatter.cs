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
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

internal static class TerminalCommandFormatter
{
    /// <summary>
    /// Terminates a command so the shell runs it, exactly once.
    /// </summary>
    /// <remarks>
    /// <para>Any terminator the caller left is removed first, then
    /// <see cref="AppConstants.TerminalSubmitKey"/> is appended. Leaving an already-terminated
    /// command alone would have been the obvious reading of "do not submit twice", and it
    /// reintroduces the defect this exists to close: a command arriving with LF - the
    /// convention every caller used until 2026-09-20 - would be forwarded intact, and LF does
    /// not submit on a ConPTY. Stripping first leaves no incorrect form reachable.</para>
    /// <para>The cost is that a caller mixing conventions is now silent rather than broken. At
    /// this boundary the operator comes first, so the debug line below is the whole of what is
    /// left of the complaint. It cannot be noisy: nothing is stripped from a caller that
    /// follows the contract, so a correct call never logs.</para>
    /// </remarks>
    public static string Submit(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        string line = command.TrimEnd('\r', '\n');
        if (line.Length != command.Length)
        {
            Heimdall.Core.Logging.FileLogger.Debug(
                "[TerminalCommandFormatter] A caller submitted a command that already carried "
                + $"{command.Length - line.Length} terminator character(s); "
                + "they were replaced with the canonical submit key.");
        }

        return line + AppConstants.TerminalSubmitKey;
    }

    private enum LocalShellKind
    {
        Cmd,
        Posix,
        PowerShell
    }

    private static string StripControlChars(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // Remove CR, LF and other C0 control chars; they can break out of the
        // intended command line even inside quotes for some shells.
        StringBuilder builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static LocalShellKind DetectLocalShell(string? shellExecutable)
    {
        // The file name, not the whole path: matching anywhere in the path made
        // C:\cmdtools\pwsh.exe a cmd shell and quoted its arguments the wrong way.
        string shellExe = Path.GetFileName(
            (shellExecutable ?? AppConstants.DefaultLocalShellExecutable).Trim()).ToLowerInvariant();
        if (shellExe.Contains("cmd", StringComparison.Ordinal))
        {
            return LocalShellKind.Cmd;
        }

        if (shellExe.Contains("wsl", StringComparison.Ordinal) || shellExe.Contains("bash", StringComparison.Ordinal))
        {
            return LocalShellKind.Posix;
        }

        return LocalShellKind.PowerShell;
    }

    private static string QuoteLocalPath(string? shellExecutable, string path)
    {
        string clean = StripControlChars(path);
        LocalShellKind shellKind = DetectLocalShell(shellExecutable);
        return shellKind switch
        {
            LocalShellKind.Cmd => "\"" + clean.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"",
            LocalShellKind.Posix => InputValidator.EscapeShellArg(clean),
            _ => "'" + clean.Replace("'", "''", StringComparison.Ordinal) + "'"
        };
    }

    public static string FormatCd(string? shellExecutable, string path)
    {
        LocalShellKind shellKind = DetectLocalShell(shellExecutable);
        string quotedPath = QuoteLocalPath(shellExecutable, path);
        return Submit(shellKind == LocalShellKind.Cmd
            ? "cd /d " + quotedPath
            : "cd " + quotedPath);
    }

    public static string FormatRun(string? shellExecutable, string path)
    {
        LocalShellKind shellKind = DetectLocalShell(shellExecutable);
        string quotedPath = QuoteLocalPath(shellExecutable, path);
        return Submit(shellKind == LocalShellKind.PowerShell
            ? "& " + quotedPath
            : quotedPath);
    }

    public static string FormatRemoteCd(string path)
    {
        return "cd " + InputValidator.EscapeShellArg(StripControlChars(path));
    }
}
