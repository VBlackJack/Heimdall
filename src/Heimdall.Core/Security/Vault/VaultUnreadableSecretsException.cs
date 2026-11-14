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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Thrown when the vault cannot be disabled because some stored values are sealed with a vault key
/// that cannot open them (another vault generation, a restored backup). The disable stopped before
/// the erase.
/// </summary>
/// <remarks>
/// Disabling again with the drop option clears those values. A value that can be read is never
/// dropped.
/// </remarks>
public sealed class VaultUnreadableSecretsException : Exception
{
    /// <summary>Create the exception.</summary>
    /// <param name="counts">The locale key of each store holding unreadable values, with how many.</param>
    /// <param name="protectionRestored">Whether everything converted was sealed again.</param>
    public VaultUnreadableSecretsException(IReadOnlyList<KeyValuePair<string, int>> counts, bool protectionRestored = true)
        : base("Some stored values are sealed with a key that cannot open them.")
    {
        ArgumentNullException.ThrowIfNull(counts);
        Counts = counts;
        ProtectionRestored = protectionRestored;
    }

    /// <summary>The locale key of each store holding unreadable values, with how many.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> Counts { get; }

    /// <summary>The total number of unreadable values.</summary>
    public int TotalCount => Counts.Sum(entry => entry.Value);

    /// <summary>Whether everything that was converted has been sealed again.</summary>
    public bool ProtectionRestored { get; }
}
