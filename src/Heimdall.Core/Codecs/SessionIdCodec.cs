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
    /// Creates a session identifier by appending an 8-character lowercase-hex
    /// discriminator so duplicate connections to the same inventory profile get
    /// independent session state.
    /// </summary>
    public static string Create(string inventoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryId);

        return $"{inventoryId}_{Guid.NewGuid().ToString("N")[..8]}";
    }

    /// <summary>
    /// The inventory profile a runtime identifier belongs to, decided with the inventory in hand.
    /// </summary>
    /// <param name="runtimeId">The identifier a session or pane is running under.</param>
    /// <param name="isInventoryId">
    /// Whether the argument names a profile the inventory holds.
    /// </param>
    /// <returns>
    /// <paramref name="runtimeId"/> itself whenever it names an inventory profile, the decoded
    /// prefix when it does not and it carries a minted discriminator, and
    /// <paramref name="runtimeId"/> unchanged otherwise.
    /// </returns>
    /// <remarks>
    /// <para><b>Exact first, and that ordering is the whole point.</b> The mint appends an
    /// underscore and eight hexadecimal characters, and nothing stops a profile identifier from
    /// having that shape already: an import preserves the identifier its file carried, so
    /// <c>prod_deadbeef</c> can be a profile in its own right. Inverting the mint on it without
    /// asking the inventory decodes it to <c>prod</c> and hands one profile's state - a trust
    /// set, most consequentially - to an unrelated one.</para>
    /// <para><b>The residual ambiguity, which the ordering narrows rather than closes.</b> If the
    /// inventory holds both <c>prod</c> and <c>prod_deadbeef</c>, then a session minted for
    /// <c>prod</c> whose discriminator happens to be exactly <c>deadbeef</c> resolves to
    /// <c>prod_deadbeef</c>. That needs a specific eight-hexadecimal-character collision out of
    /// a GUID, against an identifier that already exists; the unconditional inversion needed
    /// only the profile to exist.</para>
    /// </remarks>
    public static string ResolveInventoryId(string runtimeId, Func<string, bool> isInventoryId)
    {
        ArgumentNullException.ThrowIfNull(isInventoryId);

        if (string.IsNullOrWhiteSpace(runtimeId) || isInventoryId(runtimeId))
        {
            return runtimeId;
        }

        return TryGetInventoryId(runtimeId, out string inventoryId) ? inventoryId : runtimeId;
    }

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
