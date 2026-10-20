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

namespace Heimdall.App.Services;

/// <summary>
/// What to tell the user when a VNC server asks for credentials the client was not given.
/// </summary>
/// <remarks>
/// noVNC asks only when what it already holds is not enough: no password was stored, or the
/// server also wants a username (macOS Screen Sharing, VeNCrypt Plain, TightUnix, MSLogon). The
/// view used to answer by sending the same connect again, which can never satisfy the request and
/// built a second client behind the first on a proxy that serves one connection at a time - the
/// pane hung on "Connecting". It is a dead end, so it is reported as one - except for a password
/// alone on a session whose profile cannot hold it (a shared one), which the user can type in the
/// view (<see cref="VncPasswordFlow"/>).
/// </remarks>
internal static class VncCredentialsPolicy
{
    /// <summary>The message to show for the credential types the server asked for.</summary>
    /// <param name="requestedTypesJson">The JSON array of credential names noVNC reported, if any.</param>
    /// <param name="canTypePassword">
    /// Whether the user can type the password in the view, for a session whose profile cannot hold
    /// it: a request for a password alone then says to type it rather than to edit the profile.
    /// </param>
    internal static string MessageKeyFor(string? requestedTypesJson, bool canTypePassword = false)
    {
        if (canTypePassword && AsksForPasswordOnly(requestedTypesJson))
        {
            return "ErrorVncSharedPasswordRequired";
        }

        return RequestedTypes(requestedTypesJson).Contains("username", StringComparer.OrdinalIgnoreCase)
            ? "ErrorVncUsernameRequired"
            : "ErrorVncPasswordRequired";
    }

    /// <summary>
    /// Whether the server asked for a password alone, which a user can type for a session whose
    /// profile cannot hold it. A username as well keeps the dead end: Heimdall has no VNC username.
    /// </summary>
    internal static bool AsksForPasswordOnly(string? requestedTypesJson)
    {
        IReadOnlyList<string> types = RequestedTypes(requestedTypesJson);
        return types.Count > 0 && types.All(type => string.Equals(type, "password", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The message to show for a security failure the page reported.</summary>
    /// <param name="failure">The failure, read by <see cref="ReadSecurityFailure"/>.</param>
    /// <param name="canAsk">Whether the user can type the password in the view.</param>
    internal static string MessageKeyForSecurityFailure(VncSecurityFailure failure, bool canAsk)
    {
        if (failure.AfterPassword)
        {
            return canAsk ? "ErrorVncSharedPasswordRejected" : "ErrorVncPasswordRejected";
        }

        return string.IsNullOrWhiteSpace(failure.Reason) ? "ErrorVncSecurityRefused" : "ErrorVncSecurityRefusedReason";
    }

    /// <summary>Reads the JSON the page sends with a security failure; a malformed one counts as before the password.</summary>
    internal static VncSecurityFailure ReadSecurityFailure(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new VncSecurityFailure(false, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new VncSecurityFailure(false, null);
            }

            bool afterPassword = root.TryGetProperty("afterPassword", out JsonElement after)
                && after.ValueKind == JsonValueKind.True;
            string? reason = root.TryGetProperty("reason", out JsonElement text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
            return new VncSecurityFailure(afterPassword, reason);
        }
        catch (JsonException)
        {
            return new VncSecurityFailure(false, null);
        }
    }

    private static IReadOnlyList<string> RequestedTypes(string? requestedTypesJson)
    {
        if (string.IsNullOrWhiteSpace(requestedTypesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(requestedTypesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>A VNC sign-in the server ended with a security failure.</summary>
/// <param name="AfterPassword">Whether the page had sent the password when the server refused.</param>
/// <param name="Reason">The reason the server gave, if any.</param>
internal sealed record VncSecurityFailure(bool AfterPassword, string? Reason);
