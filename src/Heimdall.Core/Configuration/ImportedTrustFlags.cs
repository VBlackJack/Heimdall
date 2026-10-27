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

namespace Heimdall.Core.Configuration;

/// <summary>
/// One profile switch that hands a remote host something of this machine's, and that a profile
/// read from someone else's file therefore cannot turn on.
/// </summary>
/// <param name="PropertyName">The <see cref="ServerProfileDto"/> property it is stored in.</param>
/// <param name="LabelKey">Locale key of the name the import report gives it.</param>
/// <param name="IsSet">Whether the profile has it on.</param>
/// <param name="Reset">Turns it off.</param>
public sealed record ImportedTrustFlag(
    string PropertyName,
    string LabelKey,
    Func<ServerProfileDto, bool> IsSet,
    Action<ServerProfileDto> Reset);

/// <summary>
/// The denylist of trust switches every external import turns off: JSON, the legacy parsers,
/// OpenSSH, PuTTY and .rdp files alike.
/// </summary>
/// <remarks>
/// <para>Each of these lets the remote end reach into this machine: the SSH agent signs for it,
/// X11 forwarding lets it read the local display and keyboard, and drive and USB redirection
/// expose local files and devices. Whoever wrote the file decided them for a host of their
/// choosing; a rogue .rdp file with drive redirection is a known phishing vector. The user turns
/// one back on in the profile when they trust its source.</para>
/// <para>Smart card redirection is deliberately absent: smart card logon needs it, and using the
/// card still takes its PIN.</para>
/// <para>A denylist, unlike <see cref="SharedProfileSanitizer"/>, which builds a shared profile
/// from an allowlist and never sees these switches; the two are kept apart on purpose.</para>
/// </remarks>
public static class ImportedTrustFlags
{
    /// <summary>Every trust switch reset at the import boundary, in report order.</summary>
    public static IReadOnlyList<ImportedTrustFlag> Denylist { get; } =
    [
        new(
            nameof(ServerProfileDto.SshAgentForwarding),
            "A11yServerSshAgentForwarding",
            static profile => profile.SshAgentForwarding,
            static profile => profile.SshAgentForwarding = false),
        new(
            nameof(ServerProfileDto.SshX11Forwarding),
            "A11yServerSshX11Forwarding",
            static profile => profile.SshX11Forwarding,
            static profile => profile.SshX11Forwarding = false),
        new(
            nameof(ServerProfileDto.RdpRedirectDrives),
            "ServerDialogRedirectDrives",
            static profile => profile.RdpRedirectDrives,
            static profile => profile.RdpRedirectDrives = false),
        new(
            nameof(ServerProfileDto.RdpRedirectUsb),
            "ServerDialogRedirectUsb",
            static profile => profile.RdpRedirectUsb,
            static profile => profile.RdpRedirectUsb = false),
    ];

    /// <summary>
    /// Turns off every switch of <see cref="Denylist"/> on <paramref name="profile"/>.
    /// </summary>
    /// <returns>The switches that were on, in the order of <see cref="Denylist"/>.</returns>
    public static IReadOnlyList<ImportedTrustFlag> Reset(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        List<ImportedTrustFlag> wereSet = [];
        foreach (ImportedTrustFlag flag in Denylist)
        {
            if (flag.IsSet(profile))
            {
                wereSet.Add(flag);
                flag.Reset(profile);
            }
        }

        return wereSet;
    }

    /// <summary>
    /// Turns off every switch of <see cref="Denylist"/> that is on in <paramref name="updated"/>
    /// but was off in <paramref name="before"/>: an import that updates an existing profile may
    /// not turn one on, and leaves the user's own choice alone.
    /// </summary>
    /// <returns>The switches turned off, in the order of <see cref="Denylist"/>.</returns>
    public static IReadOnlyList<ImportedTrustFlag> ResetTurnedOn(ServerProfileDto before, ServerProfileDto updated)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(updated);

        List<ImportedTrustFlag> turnedOn = [];
        foreach (ImportedTrustFlag flag in Denylist)
        {
            if (flag.IsSet(updated) && !flag.IsSet(before))
            {
                turnedOn.Add(flag);
                flag.Reset(updated);
            }
        }

        return turnedOn;
    }
}
