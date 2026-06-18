/*
 * Copyright 2025 Julien Bombled
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

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Service for managing package operations (Winget and Chocolatey)
/// </summary>
public sealed class PackageManagerService : IPackageManagerService
{
    private const int DefaultTimeoutSeconds = 30;
    private static readonly Regex ValidSearchTermRegex = new(@"^[a-zA-Z0-9\s\-_.]+$", RegexOptions.Compiled);
    private readonly ILogger<PackageManagerService>? _logger;

    public PackageManagerService(ILogger<PackageManagerService>? logger = null)
    {
        _logger = logger;
    }

    public async Task<IEnumerable<PackageSearchResult>> SearchWingetPackagesAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Array.Empty<PackageSearchResult>();
        }

        var validatedSearchTerm = ValidatePackageArgument(searchTerm, nameof(searchTerm));
        var output = await ExecuteCommandAsync("winget", BuildSearchArguments("winget", validatedSearchTerm));
        return ParseWingetSearchOutput(output);
    }

    public async Task<IEnumerable<PackageSearchResult>> SearchChocolateyPackagesAsync(string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Array.Empty<PackageSearchResult>();
        }

        var validatedSearchTerm = ValidatePackageArgument(searchTerm, nameof(searchTerm));
        var output = await ExecuteCommandAsync("choco", BuildSearchArguments("choco", validatedSearchTerm));
        return ParseChocolateySearchOutput(output);
    }

    public async Task<PackageInfo?> GetWingetPackageInfoAsync(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var validatedPackageId = ValidatePackageArgument(packageId, nameof(packageId));
        var output = await ExecuteCommandAsync("winget", BuildInfoArguments("winget", validatedPackageId));
        return ParseWingetShowOutput(output, validatedPackageId);
    }

    public async Task<PackageInfo?> GetChocolateyPackageInfoAsync(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        var validatedPackageId = ValidatePackageArgument(packageId, nameof(packageId));
        var output = await ExecuteCommandAsync("choco", BuildInfoArguments("choco", validatedPackageId));
        return ParseChocolateyInfoOutput(output, validatedPackageId);
    }

    public async Task<IEnumerable<PackageSearchResult>> ListWingetInstalledPackagesAsync()
    {
        var output = await ExecuteCommandAsync("winget", BuildListArguments("winget"));
        return ParseWingetListOutput(output);
    }

    public async Task<IEnumerable<PackageSearchResult>> ListChocolateyInstalledPackagesAsync()
    {
        var output = await ExecuteCommandAsync("choco", BuildListArguments("choco"));
        return ParseChocolateyListOutput(output);
    }

    public async Task<bool> IsWingetAvailableAsync()
    {
        try
        {
            var output = await ExecuteCommandAsync("winget", "--version", timeoutSeconds: 5);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception)
        {
            // Winget not available or command failed
            return false;
        }
    }

    public async Task<bool> IsChocolateyAvailableAsync()
    {
        try
        {
            var output = await ExecuteCommandAsync("choco", "--version", timeoutSeconds: 5);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch (Exception)
        {
            // Chocolatey not available or command failed
            return false;
        }
    }

    private async Task<string> ExecuteCommandAsync(string command, string arguments, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        return await ExecuteCommandAsync(command, [arguments], timeoutSeconds).ConfigureAwait(false);
    }

    private async Task<string> ExecuteCommandAsync(string command, IReadOnlyList<string> arguments, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        try
        {
            var processStartInfo = CreateProcessStartInfo(command, arguments);

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                process.Kill();
                throw new TimeoutException(
                    $"Command '{FormatCommandForLog(command, arguments)}' timed out after {timeoutSeconds} seconds");
            }

            var output = await outputTask;
            var error = await errorTask;

            return string.IsNullOrWhiteSpace(error) ? output : output + "\n" + error;
        }
        catch (Exception ex)
        {
            // SECURITY: Don't expose detailed error information
            _logger?.LogError(ex, "Failed to execute command");
            throw new InvalidOperationException("Command execution failed");
        }
    }

    /// <summary>
    /// Validates search term or package ID to prevent command injection
    /// </summary>
    private static string ValidatePackageArgument(string term, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            throw new ArgumentException("Package query value cannot be empty.", parameterName);
        }

        var normalized = term.Trim();

        if (normalized.Length > 200)
        {
            throw new ArgumentException("Package query value exceeds the 200 character limit.", parameterName);
        }

        var firstNonWhitespace = normalized[0];
        if (firstNonWhitespace is '-' or '/')
        {
            throw new ArgumentException(
                $"Option-shaped values are not allowed for {parameterName}: {PreviewValue(term)}",
                parameterName);
        }

        if (!ValidSearchTermRegex.IsMatch(normalized))
        {
            throw new ArgumentException(
                $"Package query value contains unsupported characters: {PreviewValue(term)}",
                parameterName);
        }

        return normalized;
    }

    private static ProcessStartInfo CreateProcessStartInfo(string command, IReadOnlyList<string> arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        return processStartInfo;
    }

    private static IReadOnlyList<string> BuildSearchArguments(string command, string searchTerm)
    {
        return command switch
        {
            "winget" => ["search", "--", searchTerm],
            "choco" => ["search", "--", searchTerm],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported package manager command.")
        };
    }

    private static IReadOnlyList<string> BuildInfoArguments(string command, string packageId)
    {
        return command switch
        {
            "winget" => ["show", "--", packageId],
            "choco" => ["info", "--", packageId],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported package manager command.")
        };
    }

    private static IReadOnlyList<string> BuildListArguments(string command)
    {
        return command switch
        {
            "winget" => ["list"],
            "choco" => ["list", "--local-only"],
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported package manager command.")
        };
    }

    private static string FormatCommandForLog(string command, IReadOnlyList<string> arguments)
    {
        return $"{command} {string.Join(" ", arguments)}".TrimEnd();
    }

    private static string PreviewValue(string value)
    {
        const int MaxPreviewLength = 32;
        var preview = value.Trim();
        if (preview.Length <= MaxPreviewLength)
        {
            return preview;
        }

        return preview[..MaxPreviewLength];
    }

    private IEnumerable<PackageSearchResult> ParseWingetSearchOutput(string output)
    {
        var results = new List<PackageSearchResult>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        bool inResultsSection = false;

        foreach (var line in lines)
        {
            // Skip header lines
            if (line.Contains("Name") && line.Contains("Id") && line.Contains("Version"))
            {
                inResultsSection = true;
                continue;
            }

            if (line.Contains("---") || !inResultsSection)
            {
                continue;
            }

            // Parse result line (format: Name  Id  Version  Source)
            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length >= 3)
            {
                results.Add(new PackageSearchResult
                {
                    Name = parts[0].Trim(),
                    Id = parts[1].Trim(),
                    Version = parts[2].Trim(),
                    Source = parts.Length > 3 ? parts[3].Trim() : "winget",
                    PackageManager = PackageManager.Winget
                });
            }
        }

        return results;
    }

    private IEnumerable<PackageSearchResult> ParseChocolateySearchOutput(string output)
    {
        var results = new List<PackageSearchResult>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return results;
        }

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Parse format: packagename version [Approved] Description
            var match = Regex.Match(line, @"^(\S+)\s+(\S+)");
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                var version = match.Groups[2].Value;

                // Skip Chocolatey metadata lines
                if (name.Contains("packages found") || name.Contains("Chocolatey"))
                {
                    continue;
                }

                results.Add(new PackageSearchResult
                {
                    Id = name,
                    Name = name,
                    Version = version,
                    Source = "chocolatey",
                    PackageManager = PackageManager.Chocolatey
                });
            }
        }

        return results;
    }

    private IEnumerable<PackageSearchResult> ParseWingetListOutput(string output)
    {
        // Similar parsing to search output
        return ParseWingetSearchOutput(output);
    }

    private IEnumerable<PackageSearchResult> ParseChocolateyListOutput(string output)
    {
        // Similar parsing to search output
        return ParseChocolateySearchOutput(output);
    }

    private PackageInfo? ParseWingetShowOutput(string output, string packageId)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var info = new PackageInfo
        {
            Id = packageId,
            PackageManager = PackageManager.Winget
        };

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            switch (key)
            {
                case "name":
                    info.Name = value;
                    break;
                case "version":
                    info.Version = value;
                    break;
                case "publisher":
                    info.Publisher = value;
                    break;
                case "author":
                    info.Author = value;
                    break;
                case "description":
                    info.Description = value;
                    break;
                case "homepage":
                    info.Homepage = value;
                    break;
                case "license":
                    info.License = value;
                    break;
                case "license url":
                    info.LicenseUrl = value;
                    break;
            }
        }

        return info;
    }

    private PackageInfo? ParseChocolateyInfoOutput(string output, string packageId)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var info = new PackageInfo
        {
            Id = packageId,
            Name = packageId,
            PackageManager = PackageManager.Chocolatey
        };

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.Contains("Title:"))
            {
                info.Name = line.Split(':', 2)[1].Trim();
            }
            else if (line.Contains("Version:"))
            {
                info.Version = line.Split(':', 2)[1].Trim();
            }
            else if (line.Contains("Author:"))
            {
                info.Author = line.Split(':', 2)[1].Trim();
            }
            else if (line.Contains("Description:"))
            {
                info.Description = line.Split(':', 2)[1].Trim();
            }
        }

        return info;
    }
}
