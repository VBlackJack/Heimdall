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

using System.Text;
using System.Text.RegularExpressions;

namespace Heimdall.Core.Ssh;

/// <summary>
/// Validates and safely formats SSH startup commands (<c>RemoteCommand</c>) and POSIX environment
/// variable assignments (<c>SetEnv</c> <c>KEY=VALUE</c>) for interactive SSH shells.
/// </summary>
public static partial class SshStartupCommandPolicy
{
    public const int MaxEnvironmentVariables = 32;
    public const int MaxValueLength = 1024;
    public const int MaxStartupCommandLength = 1024;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidEnvNameRegex();

    /// <summary>
    /// Validates and parses a single <c>KEY=VALUE</c> environment assignment, rejecting invalid
    /// POSIX names, control characters, and oversized values.
    /// </summary>
    public static bool TryParseEnvAssignment(string? raw, out string name, out string value)
    {
        name = string.Empty;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        int eq = raw.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        string candidateName = raw[..eq].Trim();
        string candidateValue = raw[(eq + 1)..];
        if (!ValidEnvNameRegex().IsMatch(candidateName) || candidateValue.Length > MaxValueLength)
        {
            return false;
        }

        foreach (char c in candidateValue)
        {
            if (c is '\r' or '\n' or '\0' || (char.IsControl(c) && c != '\t'))
            {
                return false;
            }
        }

        name = candidateName;
        value = candidateValue;
        return true;
    }

    /// <summary>
    /// Validates a single-line SSH startup command (<c>RemoteCommand</c>), rejecting newlines
    /// and NUL/control characters.
    /// </summary>
    public static bool TryNormalizeStartupCommand(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Length > MaxStartupCommandLength)
        {
            return false;
        }

        foreach (char c in trimmed)
        {
            if (c is '\r' or '\n' or '\0' || (char.IsControl(c) && c != '\t'))
            {
                return false;
            }
        }

        normalized = trimmed;
        return true;
    }

    /// <summary>
    /// Builds a POSIX single-quoted <c>export KEY='val' ...</c> line so values cannot inject
    /// shell metacharacters (<c>$(...)</c>, backticks, semicolons, quotes).
    /// </summary>
    public static string? BuildPosixExportLine(IReadOnlyList<string>? envAssignments)
    {
        if (envAssignments is null || envAssignments.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (string raw in envAssignments)
        {
            if (parts.Count >= MaxEnvironmentVariables)
            {
                break;
            }

            if (TryParseEnvAssignment(raw, out string name, out string value))
            {
                string escaped = value.Replace("'", "'\\''", StringComparison.Ordinal);
                parts.Add($"{name}='{escaped}'");
            }
        }

        return parts.Count == 0 ? null : $"export {string.Join(' ', parts)}";
    }

    /// <summary>
    /// Composes the initial interactive shell payload combining validated <c>SetEnv</c> exports
    /// and an optional startup command, terminated with a newline. Returns <see langword="null"/>
    /// when neither is configured or valid.
    /// </summary>
    public static string? BuildInitialShellInput(
        IReadOnlyList<string>? envAssignments,
        string? startupCommand)
    {
        string? exportLine = BuildPosixExportLine(envAssignments);
        bool hasCmd = TryNormalizeStartupCommand(startupCommand, out string cmd);

        if (exportLine is null && !hasCmd)
        {
            return null;
        }

        var sb = new StringBuilder();
        if (exportLine is not null)
        {
            sb.Append(exportLine).Append('\n');
        }

        if (hasCmd)
        {
            sb.Append(cmd).Append('\n');
        }

        return sb.ToString();
    }
}
