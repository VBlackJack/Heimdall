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

namespace Heimdall.App.Services;

/// <summary>
/// Removes credentials, query parameters, and fragments from StoreFront URLs used by diagnostics
/// and session-event telemetry while retaining the endpoint scheme, host, port, and path.
/// </summary>
internal static class CitrixStoreFrontUrlSanitizer
{
    /// <summary>
    /// Sanitizes an absolute HTTP or HTTPS URL. Non-URL host values are returned unchanged so the
    /// graphical-session fallback policy can still resolve ordinary host names and titles.
    /// </summary>
    internal static string? Sanitize(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue) ||
            !Uri.TryCreate(rawValue, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return rawValue;
        }

        return Sanitize(uri);
    }

    /// <summary>Sanitizes a validated StoreFront URI.</summary>
    internal static string Sanitize(Uri storeFrontUri)
    {
        ArgumentNullException.ThrowIfNull(storeFrontUri);

        int port = storeFrontUri.IsDefaultPort ? -1 : storeFrontUri.Port;
        UriBuilder safeUri = new(
            storeFrontUri.Scheme,
            storeFrontUri.Host,
            port,
            storeFrontUri.AbsolutePath);
        return safeUri.Uri.AbsoluteUri;
    }
}
