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

namespace Heimdall.Ssh.Plink;

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
    /// Creates a fresh, unique session name carrying <see cref="Prefix"/>.
    /// </summary>
    public static string CreateName() => $"{Prefix}{Guid.NewGuid():N}";

    /// <summary>
    /// Whether <paramref name="sessionName"/> is a temporary size session created by Heimdall.
    /// </summary>
    public static bool IsHeimdallSession(string? sessionName) =>
        sessionName is not null && sessionName.StartsWith(Prefix, StringComparison.Ordinal);
}
