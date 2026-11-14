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

using System.Text.Json;

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Looks through a configuration document for any string sealed with the vault key, wherever it
/// sits, including in fields this build does not know.
/// </summary>
/// <remarks>
/// Only a well-formed secret blob matches: the whole frame must decode, not just begin with the
/// magic, so an ordinary string that happens to start with the same characters (a host name, say)
/// is not mistaken for a secret and cannot block a disable. The wrapped vault key and the Windows
/// Hello wrapped key carry other magics, so they never match.
/// </remarks>
public static class VaultConfigurationScan
{
    /// <summary>
    /// Whether the document at <paramref name="path"/> holds a vault-sealed string. A missing
    /// document holds none; one that cannot be parsed is reported as holding one, since nothing
    /// can be said of it and disabling the vault would then be a guess.
    /// </summary>
    /// <param name="path">The configuration document.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<bool> HoldsVaultSecretsAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        string text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return ContainsSecretBlob(document.RootElement);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>Whether <paramref name="value"/> is a well-formed secret blob sealed with a vault key.</summary>
    /// <param name="value">The candidate string.</param>
    public static bool IsVaultSecret(string? value) => VaultSecretBlob.TryDecode(value, out _);

    /// <summary>Whether any string of <paramref name="element"/>, at any depth, is a vault secret blob.</summary>
    /// <param name="element">The JSON value to look through.</param>
    public static bool ContainsSecretBlob(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return IsVaultSecret(element.GetString());
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (ContainsSecretBlob(item))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (ContainsSecretBlob(property.Value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }
}
