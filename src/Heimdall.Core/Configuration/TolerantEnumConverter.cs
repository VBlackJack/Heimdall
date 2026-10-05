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
using System.Text.Json.Serialization;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Ssh;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Reads an enum setting by name or number and falls back instead of throwing on a value this
/// build does not know; writes it by name.
/// </summary>
/// <remarks>
/// The strict converter turned one unknown value - a file written by a newer build, or a hand
/// edit - into a <see cref="JsonException"/> for the whole settings file, and the application
/// stopped at startup. The rest of the loader keeps out-of-range values and diagnoses them; an
/// enum now degrades the same way: the setting takes its default and a warning names the value.
/// </remarks>
public abstract class TolerantEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private readonly TEnum _fallback;

    /// <param name="fallback">The value used when the stored one is not a member of the enum.</param>
    protected TolerantEnumConverter(TEnum fallback)
    {
        _fallback = fallback;
    }

    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? raw;
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                raw = reader.GetString();
                if (raw is not null
                    && !int.TryParse(raw, out _)
                    && Enum.TryParse(raw, ignoreCase: true, out TEnum named)
                    && Enum.IsDefined(named))
                {
                    return named;
                }

                break;
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out int number)
                    && Enum.IsDefined(typeof(TEnum), number))
                {
                    return (TEnum)Enum.ToObject(typeof(TEnum), number);
                }

                raw = reader.TryGetInt64(out long wide) ? wide.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?";
                break;
            default:
                raw = reader.TokenType.ToString();
                reader.Skip();
                break;
        }

        FileLogger.Warn(
            $"Settings value '{raw}' is not a known {typeof(TEnum).Name}; using {_fallback} instead.");
        return _fallback;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>Tolerant converter for <see cref="SshAgentPreference"/>.</summary>
public sealed class SshAgentPreferenceJsonConverter()
    : TolerantEnumConverter<SshAgentPreference>(SshAgentPreference.AutoOpenSshFirst);

/// <summary>Tolerant converter for <see cref="BroadcastScope"/>.</summary>
public sealed class BroadcastScopeJsonConverter()
    : TolerantEnumConverter<BroadcastScope>(BroadcastScope.CurrentTab);

/// <summary>Tolerant converter for <see cref="CredentialProviderKind"/>.</summary>
public sealed class CredentialProviderKindJsonConverter()
    : TolerantEnumConverter<CredentialProviderKind>(CredentialProviderKind.Command);

/// <summary>Tolerant converter for <see cref="VaultMigrationState"/>.</summary>
public sealed class VaultMigrationStateJsonConverter()
    : TolerantEnumConverter<VaultMigrationState>(VaultMigrationState.None);
