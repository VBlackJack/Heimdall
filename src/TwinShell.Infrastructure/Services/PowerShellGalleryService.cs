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

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Infrastructure.Services;

/// <summary>
/// Service for interacting with the PowerShell Gallery
/// </summary>
public sealed class PowerShellGalleryService : IPowerShellGalleryService
{
    private readonly ICommandExecutionService _commandExecutionService;
    private readonly IActionService _actionService;
    private readonly ILogger<PowerShellGalleryService>? _logger;

    // BUGFIX: Declare Regex and dangerous characters as static readonly for better performance
    private static readonly Regex ValidNameRegex = new Regex(@"^[a-zA-Z0-9._-]+$", RegexOptions.Compiled);
    private static readonly char[] DangerousChars = { ';', '|', '&', '$', '`', '(', ')', '<', '>', '\n', '\r', '"' };

    // BUGFIX: Externalize allowed hosts list for better configuration management
    private static readonly string[] AllowedHosts = { "github.com", "www.github.com", "powershellgallery.com", "www.powershellgallery.com" };

    public PowerShellGalleryService(
        ICommandExecutionService commandExecutionService,
        IActionService actionService,
        ILogger<PowerShellGalleryService>? logger = null)
    {
        _commandExecutionService = commandExecutionService ?? throw new ArgumentNullException(nameof(commandExecutionService));
        _actionService = actionService ?? throw new ArgumentNullException(nameof(actionService));
        _logger = logger;
    }

    public async Task<IEnumerable<PowerShellModule>> SearchModulesAsync(string query, int maxResults = 50)
    {
        // SECURITY: Validate and sanitize query
        if (!ValidateModuleName(query))
        {
            return Enumerable.Empty<PowerShellModule>();
        }

        var command = $@"
            Find-Module -Name '*{EscapeForPowerShell(query)}*' -ErrorAction SilentlyContinue |
            Select-Object -First {maxResults} |
            Select-Object Name, Version, Description, Author, CompanyName, PublishedDate, DownloadCount, ProjectUri, LicenseUri, Tags |
            ConvertTo-Json -Depth 3
        ";

        var result = await _commandExecutionService.ExecuteAsync(
            command,
            Platform.Windows,
            CancellationToken.None,
            timeoutSeconds: TimeoutConstants.PowerShellGallerySearchTimeoutSeconds);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return Enumerable.Empty<PowerShellModule>();
        }

        var dtos = SafeDeserializeJsonList<PowerShellGalleryModuleDto>(result.Stdout);
        return dtos.Select(MapToModule).ToList();
    }

    public async Task<PowerShellModule?> GetModuleDetailsAsync(string moduleName)
    {
        // SECURITY: Validate module name
        if (!ValidateModuleName(moduleName))
        {
            return null;
        }

        var command = $@"
            Find-Module -Name '{EscapeForPowerShell(moduleName)}' -ErrorAction SilentlyContinue |
            Select-Object Name, Version, Description, Author, CompanyName, PublishedDate, DownloadCount, ProjectUri, LicenseUri, Tags |
            ConvertTo-Json -Depth 3
        ";

        var result = await _commandExecutionService.ExecuteAsync(
            command,
            Platform.Windows,
            CancellationToken.None,
            timeoutSeconds: TimeoutConstants.CommandTimeoutSeconds);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }

        try
        {
            var jsonModule = JsonSerializer.Deserialize<PowerShellGalleryModuleDto>(result.Stdout);
            return jsonModule == null ? null : MapToModule(jsonModule);
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Failed to parse JSON response for module {ModuleName}", moduleName);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unexpected error getting module details for {ModuleName}", moduleName);
            return null;
        }
    }

    public async Task<IEnumerable<PowerShellCommand>> GetModuleCommandsAsync(string moduleName)
    {
        // SECURITY: Validate module name
        if (!ValidateModuleName(moduleName))
        {
            return Enumerable.Empty<PowerShellCommand>();
        }

        var command = $@"
            Import-Module '{EscapeForPowerShell(moduleName)}' -ErrorAction SilentlyContinue
            Get-Command -Module '{EscapeForPowerShell(moduleName)}' -ErrorAction SilentlyContinue |
            Select-Object Name, ModuleName, CommandType |
            ConvertTo-Json -Depth 3
        ";

        var result = await _commandExecutionService.ExecuteAsync(
            command,
            Platform.Windows,
            CancellationToken.None,
            timeoutSeconds: TimeoutConstants.CommandTimeoutSeconds);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return Enumerable.Empty<PowerShellCommand>();
        }

        var dtos = SafeDeserializeJsonList<PowerShellCommandDto>(result.Stdout);
        return dtos.Select(c => new PowerShellCommand
        {
            Name = c.Name ?? string.Empty,
            ModuleName = c.ModuleName ?? string.Empty,
            CommandType = c.CommandType ?? string.Empty
        }).ToList();
    }

    public async Task<PowerShellCommand?> GetCommandHelpAsync(string commandName)
    {
        // SECURITY: Validate command name
        if (!ValidateModuleName(commandName))
        {
            return null;
        }

        var command = $@"
            Get-Help '{EscapeForPowerShell(commandName)}' -Full -ErrorAction SilentlyContinue |
            Select-Object @{{Name='Name';Expression={{$_.Name}}}},
                         @{{Name='Synopsis';Expression={{$_.Synopsis}}}},
                         @{{Name='Description';Expression={{($_.Description | Out-String).Trim()}}}},
                         @{{Name='Syntax';Expression={{($_.Syntax | Out-String).Trim()}}}},
                         @{{Name='Parameters';Expression={{$_.Parameters.Parameter | ForEach-Object {{
                            [PSCustomObject]@{{
                                Name = $_.Name
                                Type = $_.Type.Name
                                IsMandatory = $_.Required -eq $true
                                Description = ($_.Description | Out-String).Trim()
                            }}
                         }}}}}},
                         @{{Name='Examples';Expression={{$_.Examples.Example | ForEach-Object {{($_.Code | Out-String).Trim()}}}}}} |
            ConvertTo-Json -Depth 5
        ";

        var result = await _commandExecutionService.ExecuteAsync(
            command,
            Platform.Windows,
            CancellationToken.None,
            timeoutSeconds: TimeoutConstants.CommandTimeoutSeconds);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Stdout))
        {
            return null;
        }

        try
        {
            var helpDto = JsonSerializer.Deserialize<PowerShellHelpDto>(result.Stdout);
            if (helpDto == null)
                return null;

            return new PowerShellCommand
            {
                Name = helpDto.Name ?? string.Empty,
                Synopsis = helpDto.Synopsis ?? string.Empty,
                Description = helpDto.Description ?? string.Empty,
                Syntax = new List<string> { helpDto.Syntax ?? string.Empty },
                Parameters = helpDto.Parameters?.Select(p => new PowerShellParameter
                {
                    Name = p.Name ?? string.Empty,
                    Type = p.Type ?? string.Empty,
                    IsMandatory = p.IsMandatory,
                    Description = p.Description ?? string.Empty
                }).ToList() ?? new List<PowerShellParameter>(),
                Examples = helpDto.Examples?.ToList() ?? new List<string>()
            };
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Failed to parse JSON help response for command {CommandName}", commandName);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unexpected error getting help for command {CommandName}", commandName);
            return null;
        }
    }

    public async Task<bool> InstallModuleAsync(string moduleName, string scope = "CurrentUser")
    {
        // SECURITY: Validate module name
        if (!ValidateModuleName(moduleName))
        {
            return false;
        }

        // SECURITY FIX: Only allow CurrentUser scope to prevent privilege escalation
        // Installing with AllUsers scope could allow privilege escalation if user has elevated permissions
        // Force CurrentUser scope regardless of parameter value for safety
        scope = "CurrentUser";

        var command = $@"
            Install-Module -Name '{EscapeForPowerShell(moduleName)}' -Scope {scope} -Force -AllowClobber -ErrorAction Stop
        ";

        var result = await _commandExecutionService.ExecuteAsync(
            command,
            Platform.Windows,
            CancellationToken.None,
            timeoutSeconds: TimeoutConstants.PowerShellGalleryInstallTimeoutSeconds);

        return result.Success;
    }

    public async Task<bool> IsModuleInstalledAsync(string moduleName)
    {
        // SECURITY: Validate module name
        if (!ValidateModuleName(moduleName))
        {
            return false;
        }

        var command = $@"
            Get-Module -ListAvailable -Name '{EscapeForPowerShell(moduleName)}' -ErrorAction SilentlyContinue |
            Select-Object -First 1 |
            ConvertTo-Json
        ";

        var result = await _commandExecutionService.ExecuteAsync(
            command,
            Platform.Windows,
            CancellationToken.None,
            timeoutSeconds: 10);

        return result.Success && !string.IsNullOrWhiteSpace(result.Stdout) && result.Stdout.Trim() != "null";
    }

    public async Task<TwinShell.Core.Models.Action> ImportCommandAsActionAsync(PowerShellCommand command)
    {
        // Build command template
        var parameters = command.Parameters
            .Where(p => p.IsMandatory)
            .Select(p => $"-{p.Name} <{p.Name}>")
            .ToList();

        var template = $"{command.Name} {string.Join(" ", parameters)}";

        // Create action
        var action = new TwinShell.Core.Models.Action
        {
            Title = command.Name,
            Description = !string.IsNullOrWhiteSpace(command.Synopsis) ? command.Synopsis : command.Description,
            Category = $"{command.ModuleName} (Gallery)",
            Platform = Platform.Windows,
            Level = CriticalityLevel.Run,
            WindowsCommandTemplate = new CommandTemplate
            {
                CommandPattern = template,
                Parameters = command.Parameters.Select(p => new TemplateParameter
                {
                    Name = p.Name,
                    Description = p.Description,
                    Required = p.IsMandatory,
                    DefaultValue = p.DefaultValue
                }).ToList()
            },
            Tags = new List<string> { "PowerShell Gallery", command.ModuleName, command.CommandType },
            Notes = $"Imported from PowerShell module: {command.ModuleName}\n\n{command.Description}",
            IsUserCreated = true
        };

        // Add examples if available
        if (command.Examples.Any())
        {
            action.Examples = command.Examples.Select((example, index) => new CommandExample
            {
                Command = example,
                Description = $"Example {index + 1} - {command.Name}"
            }).ToList();
        }

        return await _actionService.CreateActionAsync(action);
    }

    /// <summary>
    /// Validates module/command name to prevent injection
    /// </summary>
    private static bool ValidateModuleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Module names should only contain alphanumeric, dots, dashes, and underscores
        // Max length 100 characters
        if (name.Length > 100)
            return false;

        if (!ValidNameRegex.IsMatch(name))
            return false;

        // Check for dangerous characters that could be used for injection
        if (name.Any(c => DangerousChars.Contains(c)))
            return false;

        return true;
    }

    /// <summary>
    /// Escapes string for PowerShell execution
    /// </summary>
    private static string EscapeForPowerShell(string input)
    {
        // Escape single quotes by doubling them
        // This is safe when the string is used within single quotes
        return input.Replace("'", "''");
    }

    /// <summary>
    /// Safely deserializes JSON that may be an array or single object.
    /// PowerShell's ConvertTo-Json returns an array when multiple results, but a single object for one result.
    /// </summary>
    private static List<T> SafeDeserializeJsonList<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<T>();

        try
        {
            // Try to parse as array first
            var list = JsonSerializer.Deserialize<List<T>>(json);
            return list ?? new List<T>();
        }
        catch (JsonException)
        {
            // If array parsing fails, try parsing as single object
            try
            {
                var single = JsonSerializer.Deserialize<T>(json);
                return single != null ? new List<T> { single } : new List<T>();
            }
            catch (JsonException)
            {
                return new List<T>();
            }
        }
    }

    /// <summary>
    /// Validates URI to prevent malicious URLs
    /// </summary>
    private static bool ValidateUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
            return true; // Null/empty URIs are acceptable

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var result))
            return false;

        // Only allow HTTPS for security
        if (result.Scheme != "https")
            return false;

        // SECURITY FIX: Whitelist of allowed hosts for PowerShell Gallery
        // Use exact match or proper subdomain validation to prevent malicious domains
        // like 'fakepowershellgallery.com' from being accepted
        return AllowedHosts.Any(host =>
            result.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            (result.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase) &&
             result.Host.Length > host.Length + 1));
    }

    private static PowerShellModule MapToModule(PowerShellGalleryModuleDto dto)
    {
        // SECURITY: Validate URIs before storing
        var projectUri = ValidateUri(dto.ProjectUri) ? dto.ProjectUri : null;
        var licenseUri = ValidateUri(dto.LicenseUri) ? dto.LicenseUri : null;

        return new PowerShellModule
        {
            Name = dto.Name ?? string.Empty,
            Version = dto.Version ?? string.Empty,
            Description = dto.Description ?? string.Empty,
            Author = dto.Author ?? string.Empty,
            CompanyName = dto.CompanyName,
            DownloadCount = dto.DownloadCount ?? 0,
            PublishedDate = dto.PublishedDate ?? DateTime.MinValue,
            Tags = dto.Tags?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList() ?? new List<string>(),
            ProjectUri = projectUri,
            LicenseUri = licenseUri
        };
    }

    // DTOs for JSON deserialization
    private sealed class PowerShellGalleryModuleDto
    {
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? CompanyName { get; set; }
        public long? DownloadCount { get; set; }
        public DateTime? PublishedDate { get; set; }
        public string? Tags { get; set; }
        public string? ProjectUri { get; set; }
        public string? LicenseUri { get; set; }
    }

    private sealed class PowerShellCommandDto
    {
        public string? Name { get; set; }
        public string? ModuleName { get; set; }
        public string? CommandType { get; set; }
    }

    private sealed class PowerShellHelpDto
    {
        public string? Name { get; set; }
        public string? Synopsis { get; set; }
        public string? Description { get; set; }
        public string? Syntax { get; set; }
        public List<PowerShellParameterDto>? Parameters { get; set; }
        public List<string>? Examples { get; set; }
    }

    private sealed class PowerShellParameterDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public bool IsMandatory { get; set; }
        public string? Description { get; set; }
    }
}
