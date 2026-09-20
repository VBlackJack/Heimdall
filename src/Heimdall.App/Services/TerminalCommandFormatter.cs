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
    /// The byte that submits a line to a console shell: carriage return, what the Enter key
    /// sends.
    /// </summary>
    /// <remarks>
    /// Measured 2026-09-20 against a live ConPTY: a command terminated with LF leaves Windows
    /// PowerShell on its "&gt;&gt; " continuation prompt with the line unexecuted, while the
    /// same command terminated with CR runs and returns a fresh prompt. The file browser's
    /// "navigate here" and "run in shell" were typing their command and never sending it.
    /// </remarks>
    private const string SubmitKey = "\r";

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
        return shellKind == LocalShellKind.Cmd
            ? "cd /d " + quotedPath + SubmitKey
            : "cd " + quotedPath + SubmitKey;
    }

    public static string FormatRun(string? shellExecutable, string path)
    {
        LocalShellKind shellKind = DetectLocalShell(shellExecutable);
        string quotedPath = QuoteLocalPath(shellExecutable, path);
        return shellKind == LocalShellKind.PowerShell
            ? "& " + quotedPath + SubmitKey
            : quotedPath + SubmitKey;
    }

    public static string FormatRemoteCd(string path)
    {
        return "cd " + InputValidator.EscapeShellArg(StripControlChars(path));
    }
}
