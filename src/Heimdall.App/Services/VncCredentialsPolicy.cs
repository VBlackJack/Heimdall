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
/// pane hung on "Connecting". It is a dead end, so it is reported as one.
/// </remarks>
internal static class VncCredentialsPolicy
{
    /// <summary>The message to show for the credential types the server asked for.</summary>
    /// <param name="requestedTypesJson">The JSON array of credential names noVNC reported, if any.</param>
    internal static string MessageKeyFor(string? requestedTypesJson)
    {
        return RequestedTypes(requestedTypesJson).Contains("username", StringComparer.OrdinalIgnoreCase)
            ? "ErrorVncUsernameRequired"
            : "ErrorVncPasswordRequired";
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
