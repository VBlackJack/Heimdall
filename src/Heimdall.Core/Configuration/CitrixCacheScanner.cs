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

using System.IO;
using System.Xml.Linq;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Scans the local Citrix Workspace SelfService cache XML files to discover
/// published applications and desktops. Each resource contains a pre-authenticated
/// launch command line that can be passed directly to SelfService.exe.
/// </summary>
/// <remarks>
/// Cache location: %LocalAppData%\Citrix\SelfService\*_Cache.xml
/// Each store configured in Citrix Workspace produces its own cache file.
/// </remarks>
public static class CitrixCacheScanner
{
    private const string ServerStateElementName = "ServerState";
    private const string SubscribedServerState = "Subscribed";
    private const string CitrixIdSwitch = "-CitrixID";

    /// <summary>
    /// Scans all Citrix SelfService cache XML files and returns discovered resources.
    /// </summary>
    /// <returns>List of discovered Citrix resources with launch metadata.</returns>
    public static CitrixScanResult Scan()
    {
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Citrix", "SelfService");

        return ScanDirectory(cacheDir);
    }

    internal static CitrixScanResult ScanDirectory(string cacheDir)
    {
        CitrixScanResult result = new CitrixScanResult();

        if (!Directory.Exists(cacheDir))
        {
            result.Warnings.Add("Citrix SelfService cache directory not found.");
            return result;
        }

        string[] cacheFiles = Directory.GetFiles(cacheDir, "*_Cache.xml");
        if (cacheFiles.Length == 0)
        {
            result.Warnings.Add("No Citrix cache files found. Open Citrix Workspace and connect to a store first.");
            return result;
        }

        foreach (string file in cacheFiles)
        {
            try
            {
                ParseCacheFile(file, result);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Converts discovered Citrix resources into Heimdall server profiles.
    /// </summary>
    public static List<ServerProfileDto> ToServerProfiles(IReadOnlyList<CitrixResource> resources)
    {
        return resources.Select(r => new ServerProfileDto
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = r.FriendlyName,
            RemoteServer = string.IsNullOrWhiteSpace(r.StoreFrontUrl) ? "Citrix Local" : r.StoreFrontUrl,
            ConnectionType = "Citrix",
            Group = string.IsNullOrWhiteSpace(r.Category)
                ? null
                : $"Citrix/{r.Category.Replace('\\', '/')}",
            CitrixId = r.CitrixId,
            CitrixAppName = r.FriendlyName,
            CitrixStoreFrontUrl = r.StoreFrontUrl,
            CitrixLaunchCommandLine = r.LaunchCommandLine,
            CitrixUseSso = true,
            UseDirectConnection = true,
        }).ToList();
    }

    /// <summary>
    /// Extracts the stable Citrix resource identifier passed to the <c>-CitrixID</c> flag
    /// in a SelfService launch command line, or <see langword="null"/> when absent or empty.
    /// </summary>
    internal static string? ExtractCitrixId(string launchCommandLine)
    {
        if (string.IsNullOrWhiteSpace(launchCommandLine))
        {
            return null;
        }

        ReadOnlySpan<char> span = launchCommandLine.AsSpan();
        int searchOffset = 0;

        while (searchOffset < span.Length)
        {
            int matchOffset = span[searchOffset..].IndexOf(CitrixIdSwitch, StringComparison.OrdinalIgnoreCase);
            if (matchOffset < 0)
            {
                return null;
            }

            int flagStart = searchOffset + matchOffset;
            int flagEnd = flagStart + CitrixIdSwitch.Length;

            bool validLeftBoundary = flagStart == 0 || char.IsWhiteSpace(span[flagStart - 1]);
            bool validRightBoundary = flagEnd == span.Length || char.IsWhiteSpace(span[flagEnd]);

            if (!validLeftBoundary || !validRightBoundary)
            {
                searchOffset = flagEnd;
                continue;
            }

            ReadOnlySpan<char> remainder = span[flagEnd..].TrimStart();
            if (remainder.IsEmpty)
            {
                return null;
            }

            if (remainder[0] == '"')
            {
                ReadOnlySpan<char> afterOpenQuote = remainder[1..];
                int closeQuoteIndex = afterOpenQuote.IndexOf('"');
                if (closeQuoteIndex < 0)
                {
                    return null;
                }

                ReadOnlySpan<char> quotedValue = afterOpenQuote[..closeQuoteIndex].Trim();
                return quotedValue.IsEmpty ? null : quotedValue.ToString();
            }

            int tokenEnd = 0;
            while (tokenEnd < remainder.Length && !char.IsWhiteSpace(remainder[tokenEnd]))
            {
                tokenEnd++;
            }

            ReadOnlySpan<char> unquotedValue = remainder[..tokenEnd];
            if (unquotedValue.IsEmpty || unquotedValue[0] == '-')
            {
                return null;
            }

            return unquotedValue.ToString();
        }

        return null;
    }

    private static void ParseCacheFile(string filePath, CitrixScanResult result)
    {
        var xmlSettings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null
        };
        using var reader = System.Xml.XmlReader.Create(filePath, xmlSettings);
        var doc = XDocument.Load(reader);

        // Use LocalName to be namespace-agnostic (future Citrix versions may add xmlns)
        var resources = doc.Descendants().Where(e => e.Name.LocalName == "resource");

        // Try to extract store URL from the launch command lines
        string? storeUrl = null;

        foreach (var resource in resources)
        {
            var serverState = El(resource, ServerStateElementName);
            if (!string.IsNullOrWhiteSpace(serverState)
                && !string.Equals(serverState, SubscribedServerState, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var friendlyName = El(resource, "FriendlyName");
            var description = El(resource, "Description");
            var resourceType = El(resource, "resourceType");
            var category = El(resource, "Category");
            var launchCmd = El(resource, "LaunchCommandLine");
            var icaUrl = El(resource, "icaLaunchUrl");

            if (string.IsNullOrWhiteSpace(friendlyName) || string.IsNullOrWhiteSpace(launchCmd))
            {
                continue;
            }

            // Extract store URL from icaLaunchUrl if available
            if (!string.IsNullOrWhiteSpace(icaUrl) && storeUrl is null)
            {
                try
                {
                    var uri = new Uri(icaUrl);
                    storeUrl = $"{uri.Scheme}://{uri.Host}";
                }
                catch (Exception ex) { Heimdall.Core.Logging.FileLogger.Warn($"[CitrixCacheScanner] malformed URL: {ex.Message}"); }
            }

            result.Resources.Add(new CitrixResource
            {
                CitrixId = ExtractCitrixId(launchCmd),
                FriendlyName = friendlyName,
                Description = description,
                ResourceType = resourceType ?? "Application",
                Category = category,
                LaunchCommandLine = launchCmd,
                IcaLaunchUrl = icaUrl,
                StoreFrontUrl = storeUrl,
                SourceFile = Path.GetFileName(filePath)
            });
        }
    }

    /// <summary>Namespace-agnostic element value extraction.</summary>
    private static string? El(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();
}

/// <summary>
/// Result of scanning Citrix SelfService cache files.
/// </summary>
public sealed class CitrixScanResult
{
    /// <summary>Discovered Citrix published applications and desktops.</summary>
    public List<CitrixResource> Resources { get; } = [];

    /// <summary>Non-fatal warnings during scan.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// A single Citrix published resource (application or desktop) discovered from cache.
/// </summary>
public sealed class CitrixResource
{
    /// <summary>Stable Citrix resource identifier extracted from <c>-CitrixID</c> (e.g., <c>store@@App</c>).</summary>
    public string? CitrixId { get; set; }

    /// <summary>Display name of the application (e.g., "Excel 2024").</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>"Application" or "Desktop".</summary>
    public string ResourceType { get; set; } = "Application";

    /// <summary>Category path for folder grouping (e.g., "COMMONAPPS - NIGHT - PCI").</summary>
    public string? Category { get; set; }

    /// <summary>Full SelfService.exe launch arguments (pre-authenticated).</summary>
    public string LaunchCommandLine { get; set; } = string.Empty;

    /// <summary>Direct ICA launch URL (may be expired).</summary>
    public string? IcaLaunchUrl { get; set; }

    /// <summary>StoreFront server URL extracted from ICA URL.</summary>
    public string? StoreFrontUrl { get; set; }

    /// <summary>Source cache file name.</summary>
    public string? SourceFile { get; set; }
}
