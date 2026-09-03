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
    /// <remarks>
    /// <para><b>This mints a string and nothing else. Which profile a session belongs to is not
    /// answerable from here.</b> Three attempts tried: reading the identifier's shape, asking the
    /// inventory, and keeping a process-wide record of every mint. Each was defeated, and the
    /// last one by an import - an imported profile keeps the identifier its file carried, and a
    /// session identifier is written to the log, so a profile can arrive named exactly like some
    /// earlier session and be recognised as it. A string carries no role.</para>
    /// <para>The profile a pane belongs to is recorded where the identifier is assigned, by
    /// <c>ServerProfileDto.AdoptSessionIdentity</c>, and read back through
    /// <c>ServerProfileDto.InventoryProfileId</c>.</para>
    /// </remarks>
    public static string Create(string inventoryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryId);

        return $"{inventoryId}_{Guid.NewGuid().ToString("N")[..8]}";
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
