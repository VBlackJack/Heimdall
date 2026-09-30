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
using System.Globalization;
using System.Text.RegularExpressions;

namespace Heimdall.Ssh.Plink;

/// <summary>
/// The Heimdall process that created a temporary size session: its id and its start time, so a
/// reused process id is not mistaken for the owner.
/// </summary>
/// <param name="ProcessId">Owner process id.</param>
/// <param name="StartTimeUtcTicks">Owner process start time, UTC ticks.</param>
public readonly record struct PlinkSizeSessionOwner(int ProcessId, long StartTimeUtcTicks)
{
    private static readonly Lazy<PlinkSizeSessionOwner> CurrentOwner = new(() =>
    {
        using Process current = Process.GetCurrentProcess();
        return new PlinkSizeSessionOwner(current.Id, current.StartTime.ToUniversalTime().Ticks);
    });

    /// <summary>This process.</summary>
    public static PlinkSizeSessionOwner Current => CurrentOwner.Value;
}

/// <summary>
/// Defines the naming contract for the temporary PuTTY saved sessions that carry the initial
/// terminal size to Plink through <c>-load</c>.
/// </summary>
/// <remarks>
/// <para>Windows Plink takes the size it requests for the remote PTY only from its configuration
/// (<c>TermWidth</c>/<c>TermHeight</c>), never from a console, and a pipe-mode launch has no
/// console anyway. A saved session is the only input that reaches those two values.</para>
/// <para>Names use only <c>[A-Za-z0-9-]</c>, so PuTTY stores them unescaped and the registry key
/// name is the session name. The janitor and the PuTTY import both recognise a Heimdall session
/// by <see cref="IsHeimdallSession"/>, with an ordinal comparison: the prefix is written with this
/// exact casing, so a user session that only resembles it case-insensitively is not claimed.</para>
/// </remarks>
public static class PlinkSizeSessionNaming
{
    /// <summary>
    /// Canonical prefix of every temporary size session.
    /// </summary>
    public const string Prefix = "HeimdallPtySize-";

    /// <summary>
    /// Registry path, under HKCU, where PuTTY keeps its saved sessions.
    /// </summary>
    public const string SessionsRegistryPath = @"Software\SimonTatham\PuTTY\Sessions";

    /// <summary>
    /// Registry key name of PuTTY's "Default Settings" session, in PuTTY's own escaped form.
    /// </summary>
    public const string DefaultSettingsSessionName = "Default%20Settings";

    /// <summary>
    /// Creates a fresh, unique session name carrying <see cref="Prefix"/> and naming this
    /// process as its owner.
    /// </summary>
    public static string CreateName() => CreateName(PlinkSizeSessionOwner.Current);

    /// <summary>
    /// Creates a fresh, unique session name carrying <see cref="Prefix"/> and naming
    /// <paramref name="owner"/>: <c>HeimdallPtySize-p{pid}-t{start ticks, hex}-{guid}</c>.
    /// </summary>
    /// <remarks>
    /// The owner is in the name, not in a value, so the janitor decides from the enumeration
    /// alone and never reads a key it may be about to delete. Decimal digits, lower-case hex and
    /// hyphens keep the name inside <c>[A-Za-z0-9-]</c>.
    /// </remarks>
    public static string CreateName(PlinkSizeSessionOwner owner) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Prefix}{ProcessIdTag}{owner.ProcessId}-{StartTimeTag}{owner.StartTimeUtcTicks:x}-{Guid.NewGuid():N}");

    /// <summary>
    /// Reads the owner out of a name made by <see cref="CreateName(PlinkSizeSessionOwner)"/>.
    /// Anything else, including the owner-less names of earlier releases, is refused.
    /// </summary>
    public static bool TryParseOwner(string? sessionName, out PlinkSizeSessionOwner owner)
    {
        owner = default;
        if (sessionName is null)
        {
            return false;
        }

        Match match = OwnedNamePattern.Match(sessionName);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(match.Groups[2].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long ticks))
        {
            return false;
        }

        owner = new PlinkSizeSessionOwner(processId, ticks);
        return true;
    }

    private const string ProcessIdTag = "p";
    private const string StartTimeTag = "t";

    /// <summary>
    /// The exact owned-name format: at most ten decimal digits of process id, at most sixteen
    /// hex digits of start ticks, and a 32-digit GUID. Ordinal and case-sensitive like
    /// <see cref="IsHeimdallSession"/>.
    /// </summary>
    private static readonly Regex OwnedNamePattern = new(
        "^" + Regex.Escape(Prefix) + ProcessIdTag + "([0-9]{1,10})-" + StartTimeTag + "([0-9a-f]{1,16})-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="sessionName"/> is a temporary size session created by Heimdall.
    /// </summary>
    public static bool IsHeimdallSession(string? sessionName) =>
        sessionName is not null && sessionName.StartsWith(Prefix, StringComparison.Ordinal);
}
