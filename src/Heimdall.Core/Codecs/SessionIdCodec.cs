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

namespace Heimdall.Core.Codecs;

public static class SessionIdCodec
{
    /// <summary>
    /// Every mint this process has made, so <see cref="ResolveInventoryId"/> can answer from a
    /// record of what happened instead of from the shape of a string.
    /// </summary>
    private static readonly SessionMintLedger Ledger = new(MintLedgerCapacity);

    /// <summary>
    /// How many mints are remembered. Reached only by a process that has opened this many
    /// sessions without restarting, and exceeding it costs a re-asked certificate, never a
    /// misfiled one - see <see cref="SessionMintLedger"/>.
    /// </summary>
    internal const int MintLedgerCapacity = 4096;

    /// <summary>
    /// Creates a session identifier by appending an 8-character lowercase-hex
    /// discriminator so duplicate connections to the same inventory profile get
    /// independent session state.
    /// </summary>
    /// <remarks>
    /// The mint is recorded, because this is the only instant at which the profile a session
    /// identifier belongs to is known rather than inferred. Every caller that needs to invert it
    /// later reaches <see cref="ResolveInventoryId"/>, which reads that record.
    /// </remarks>
    public static string Create(string inventoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryId);

        string sessionId = $"{inventoryId}_{Guid.NewGuid().ToString("N")[..8]}";
        Ledger.Record(sessionId, inventoryId);
        return sessionId;
    }

    /// <summary>
    /// The inventory profile a runtime identifier belongs to.
    /// </summary>
    /// <param name="runtimeId">The identifier a session or pane is running under.</param>
    /// <returns>
    /// The profile <paramref name="runtimeId"/> was minted for when this process minted it, and
    /// <paramref name="runtimeId"/> itself otherwise.
    /// </returns>
    /// <remarks>
    /// <para><b>Answered from the mint, not from the identifier's shape, and that is the whole
    /// point.</b> The mint appends an underscore and eight hexadecimal characters, and nothing
    /// stops a profile identifier from having that shape already: an import preserves the
    /// identifier its file carried, so <c>prod_deadbeef</c> can be a profile in its own right.
    /// Inverting the mint on it decodes it to <c>prod</c> and hands one profile's state - a
    /// trust set, most consequentially - to an unrelated one.</para>
    /// <para><b>Consulting the inventory instead was tried and is not sufficient.</b> Looking the
    /// exact identifier up first and inverting only when it named no profile still reads
    /// <c>prod_deadbeef</c> as a mint for <c>prod</c> the moment that profile stops being in the
    /// inventory - and a profile can be deleted while a connection it started is still in flight,
    /// because deleting it does not end that connection. The certificate question then still
    /// carries the deleted profile's name while the approval is written under the other one. No
    /// refinement of the lookup closes that: the inventory cannot distinguish an identifier that
    /// was minted from one that was merely removed, since neither is in it. The distinction
    /// exists only where the mint happened.</para>
    /// <para><b>The residual ambiguity, and it is now the only one.</b> A mint for <c>prod</c>
    /// whose discriminator happens to be exactly <c>deadbeef</c> produces the string
    /// <c>prod_deadbeef</c>, which is recorded as a mint even though a profile of that name
    /// exists; that profile's own approvals would then be filed under <c>prod</c> for as long as
    /// the record lives. It needs a specific eight-hexadecimal-character collision out of a GUID
    /// against an identifier that already exists. Shape-based inversion needed only the profile
    /// to exist, and the inventory lookup needed only the profile to be deleted.</para>
    /// </remarks>
    public static string ResolveInventoryId(string runtimeId) => Ledger.ResolveOrigin(runtimeId);

    /// <summary>
    /// Attempts to recover the inventory identifier from a generated session identifier.
    /// </summary>
    public static bool TryGetInventoryId(string sessionId, out string inventoryId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            inventoryId = sessionId;
            return false;
        }

        int separatorIndex = sessionId.LastIndexOf('_');
        if (separatorIndex <= 0 || separatorIndex + 9 != sessionId.Length)
        {
            inventoryId = sessionId;
            return false;
        }

        for (int index = separatorIndex + 1; index < sessionId.Length; index++)
        {
            char value = sessionId[index];
            bool isHex =
                (value >= '0' && value <= '9') ||
                (value >= 'a' && value <= 'f') ||
                (value >= 'A' && value <= 'F');

            if (!isHex)
            {
                inventoryId = sessionId;
                return false;
            }
        }

        inventoryId = sessionId[..separatorIndex];
        return true;
    }
}
