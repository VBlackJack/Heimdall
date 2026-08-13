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

namespace Heimdall.Core.Import;

/// <summary>
/// Best-effort parser for Microsoft .rdp text files.
/// </summary>
public static class RdpFileParser
{
    /// <summary>
    /// Parses the textual content of a .rdp file into a curated schema.
    /// Does not throw on malformed content.
    /// </summary>
    public static RdpFileSchema Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        string? fullAddress = null;
        string? alternateFullAddress = null;
        string? username = null;
        int? audioMode = null;
        bool? redirectClipboard = null;
        bool? redirectPrinters = null;
        bool? redirectSmartCards = null;
        string? drivesToRedirect = null;
        int? screenModeId = null;
        bool? useMultiMon = null;
        int? desktopWidth = null;
        int? desktopHeight = null;
        int? sessionBpp = null;
        int? authenticationLevel = null;
        int? enableCredSspSupport = null;
        string? gatewayHostname = null;
        int? gatewayUsageMethod = null;
        var hasPasswordBlob = false;
        var unknownKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TrySplitLine(line, out var key, out var type, out var value))
            {
                continue;
            }

            var normalizedKey = key.Trim();
            if (string.IsNullOrWhiteSpace(normalizedKey))
            {
                continue;
            }

            var lowerKey = normalizedKey.ToLowerInvariant();

            if (string.Equals(lowerKey, "password 51", StringComparison.OrdinalIgnoreCase)
                && string.Equals(type, "b", StringComparison.OrdinalIgnoreCase))
            {
                hasPasswordBlob = true;
                continue;
            }

            switch (lowerKey)
            {
                case "full address":
                    if (IsStringType(type))
                    {
                        fullAddress = value.Trim();
                    }
                    else
                    {
                        unknownKeys[lowerKey] = value;
                    }

                    break;

                case "alternate full address":
                    if (IsStringType(type))
                    {
                        alternateFullAddress = value.Trim();
                    }
                    else
                    {
                        unknownKeys[lowerKey] = value;
                    }

                    break;

                case "username":
                    if (IsStringType(type))
                    {
                        username = value.Trim();
                    }
                    else
                    {
                        unknownKeys[lowerKey] = value;
                    }

                    break;

                case "audiomode":
                    audioMode = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "redirectclipboard":
                    redirectClipboard = TryParseBool(type, value, lowerKey, unknownKeys);
                    break;

                case "redirectprinters":
                    redirectPrinters = TryParseBool(type, value, lowerKey, unknownKeys);
                    break;

                case "redirectsmartcards":
                    redirectSmartCards = TryParseBool(type, value, lowerKey, unknownKeys);
                    break;

                case "drivestoredirect":
                    if (IsStringType(type))
                    {
                        drivesToRedirect = value.Trim();
                    }
                    else
                    {
                        unknownKeys[lowerKey] = value;
                    }

                    break;

                case "screen mode id":
                    screenModeId = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "use multimon":
                    useMultiMon = TryParseBool(type, value, lowerKey, unknownKeys);
                    break;

                case "desktopwidth":
                    desktopWidth = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "desktopheight":
                    desktopHeight = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "session bpp":
                    sessionBpp = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "authentication level":
                    authenticationLevel = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "enablecredsspsupport":
                    enableCredSspSupport = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                case "gatewayhostname":
                    if (IsStringType(type))
                    {
                        gatewayHostname = value.Trim();
                    }
                    else
                    {
                        unknownKeys[lowerKey] = value;
                    }

                    break;

                case "gatewayusagemethod":
                    gatewayUsageMethod = TryParseInt(type, value, lowerKey, unknownKeys);
                    break;

                default:
                    unknownKeys[lowerKey] = value;
                    break;
            }
        }

        return new RdpFileSchema
        {
            FullAddress = fullAddress,
            AlternateFullAddress = alternateFullAddress,
            Username = username,
            AudioMode = audioMode,
            RedirectClipboard = redirectClipboard,
            RedirectPrinters = redirectPrinters,
            RedirectSmartCards = redirectSmartCards,
            DrivesToRedirect = drivesToRedirect,
            ScreenModeId = screenModeId,
            UseMultiMon = useMultiMon,
            DesktopWidth = desktopWidth,
            DesktopHeight = desktopHeight,
            SessionBpp = sessionBpp,
            AuthenticationLevel = authenticationLevel,
            EnableCredSspSupport = enableCredSspSupport,
            GatewayHostname = gatewayHostname,
            GatewayUsageMethod = gatewayUsageMethod,
            HasPasswordBlob = hasPasswordBlob,
            UnknownKeys = unknownKeys
        };
    }

    private static bool TrySplitLine(string line, out string key, out string type, out string value)
    {
        key = string.Empty;
        type = string.Empty;
        value = string.Empty;

        var firstColon = line.IndexOf(':');
        if (firstColon <= 0)
        {
            return false;
        }

        var secondColon = line.IndexOf(':', firstColon + 1);
        if (secondColon <= firstColon)
        {
            return false;
        }

        key = line[..firstColon];
        type = line.Substring(firstColon + 1, secondColon - firstColon - 1);
        value = secondColon + 1 < line.Length ? line[(secondColon + 1)..] : string.Empty;
        return true;
    }

    private static int? TryParseInt(
        string type,
        string value,
        string key,
        IDictionary<string, string> unknownKeys)
    {
        if (!string.Equals(type, "i", StringComparison.OrdinalIgnoreCase))
        {
            unknownKeys[key] = value;
            return null;
        }

        if (int.TryParse(value.Trim(), out var parsed))
        {
            return parsed;
        }

        unknownKeys[key] = value;
        return null;
    }

    private static bool? TryParseBool(
        string type,
        string value,
        string key,
        IDictionary<string, string> unknownKeys)
    {
        var parsed = TryParseInt(type, value, key, unknownKeys);
        return parsed.HasValue ? parsed.Value != 0 : null;
    }

    private static bool IsStringType(string type) =>
        string.Equals(type, "s", StringComparison.OrdinalIgnoreCase);
}
