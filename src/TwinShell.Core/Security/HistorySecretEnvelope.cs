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

using TwinShell.Core.Interfaces;

namespace TwinShell.Core.Security;

/// <summary>
/// Wraps the command-history fields that can carry what the user typed, and recognizes
/// rows written before they were wrapped.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a marker of our own.</b> The obvious discriminator would be the protector's own
/// format check, but the application initializes its protector without an integrity key on
/// some installs, and the value it then produces is bare base64 with nothing to recognize
/// it by - indistinguishable from a stored command. This type therefore owns a prefix and
/// is the only thing that writes it, so "is this row wrapped?" has one answer that does not
/// depend on which form the protector happened to choose.
/// </para>
/// <para>
/// <b>Rows written before this existed hold a command pattern in clear.</b> They are
/// returned unchanged rather than migrated: a history row is a disposable record of
/// something already done, and rewriting a table of them would be a second migration path
/// through a security boundary for no gain. The same reasoning covers a wrapped row whose
/// key is gone - it is reported unreadable, never repaired.
/// </para>
/// <para>
/// A pre-existing plain row that happened to begin with the marker would be read as
/// wrapped, fail to open, and show as unreadable. That is the benign end of the trade: the
/// row is disposable, and the alternative - no marker at all - loses the ability to tell
/// the two apart in every row rather than in an implausible one.
/// </para>
/// </remarks>
public static class HistorySecretEnvelope
{
    /// <summary>
    /// Prefix identifying a wrapped value. Owned by this type; nothing else writes it.
    /// </summary>
    public const string Marker = "hist1:";

    /// <summary>
    /// True when <paramref name="stored"/> was written by <see cref="Seal"/>.
    /// </summary>
    public static bool IsSealed(string? stored) =>
        stored is not null && stored.StartsWith(Marker, StringComparison.Ordinal);

    /// <summary>
    /// Wraps <paramref name="plainText"/> for storage.
    /// </summary>
    /// <exception cref="Exception">
    /// Propagated from <paramref name="protector"/> when it cannot seal right now. The
    /// caller decides what to do without the write; this type does not invent a fallback.
    /// </exception>
    public static string Seal(string plainText, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentNullException.ThrowIfNull(protector);

        return Marker + protector.Protect(plainText);
    }

    /// <summary>
    /// Reads a stored value: a wrapped one is opened, one written before wrapping existed
    /// is returned as it stands, and a wrapped one that cannot be opened returns
    /// <see langword="null"/>.
    /// </summary>
    public static string? Open(string? stored, ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        if (stored is null)
        {
            return null;
        }

        if (!IsSealed(stored))
        {
            return stored;
        }

        return protector.Unprotect(stored[Marker.Length..]);
    }
}
