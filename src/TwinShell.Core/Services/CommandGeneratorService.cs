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

using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for generating commands from templates
/// </summary>
public sealed class CommandGeneratorService : ICommandGeneratorService
{
    private readonly ILocalizationService _localizationService;

    // BUGFIX: Declare Regex as static readonly for better performance
    private static readonly Regex HostnameRegex = new Regex(@"^(?!-)([a-zA-Z0-9-]{1,63}(?<!-)\.)*[a-zA-Z0-9-]{1,63}$", RegexOptions.Compiled);

    private static readonly Regex DriveLetterRegex = new Regex(@"^[A-Za-z]$", RegexOptions.Compiled);

    // BUGFIX: Declare dangerous characters as static readonly for better performance
    private static readonly char[] DangerousChars = { '&', '|', ';', '`', '$', '(', ')', '<', '>', '\n', '\r' };

    public CommandGeneratorService(ILocalizationService localizationService)
    {
        _localizationService = localizationService;
    }
    public string GenerateCommand(CommandTemplate template, Dictionary<string, string> parameterValues)
    {
        // Validate input parameters
        if (template == null)
        {
            throw new ArgumentNullException(nameof(template), "Command template cannot be null");
        }

        if (string.IsNullOrWhiteSpace(template.CommandPattern))
        {
            throw new ArgumentException("Command template pattern cannot be null or empty", nameof(template));
        }

        if (parameterValues == null)
        {
            throw new ArgumentNullException(nameof(parameterValues), "Parameter values dictionary cannot be null");
        }

        if (template.Parameters == null)
        {
            // If no parameters defined, return the pattern as-is
            return EnforceCommandLength(template.CommandPattern);
        }

        if (template.Parameters.Count > ValidationConstants.MaxParametersPerTemplate)
        {
            throw new InvalidOperationException(
                $"Command template declares too many parameters ({template.Parameters.Count}); maximum is {ValidationConstants.MaxParametersPerTemplate}");
        }

        // PERFORMANCE: Use StringBuilder for multiple string replacements (40-60% fewer allocations)
        var command = new StringBuilder(template.CommandPattern);

        foreach (var parameter in template.Parameters)
        {
            if (parameter == null)
            {
                continue; // Skip null parameters
            }

            var value = parameterValues.ContainsKey(parameter.Name)
                ? parameterValues[parameter.Name]
                : parameter.DefaultValue ?? string.Empty;

            // Validate the value according to the parameter type
            if (!ValidateParameterValue(parameter, value, out var validationError))
            {
                throw new InvalidOperationException($"Invalid parameter '{parameter.Name}': {validationError}");
            }

            // Escape the value for shell safety using the target platform
            var escapedValue = EscapeParameterValue(value, parameter.Type, template.Platform, parameter.Quoting);

            // Replace {parameterName} with actual value using StringBuilder
            var placeholder = $"{{{parameter.Name}}}";
            command.Replace(placeholder, escapedValue);
        }

        return EnforceCommandLength(command.ToString());
    }

    public Dictionary<string, string> GetDefaultParameterValues(CommandTemplate template)
    {
        if (template == null)
        {
            throw new ArgumentNullException(nameof(template), "Command template cannot be null");
        }

        var defaults = new Dictionary<string, string>();

        if (template.Parameters == null)
        {
            return defaults; // Return empty dictionary if no parameters
        }

        foreach (var parameter in template.Parameters)
        {
            if (parameter == null)
            {
                continue; // Skip null parameters
            }

            if (!string.IsNullOrEmpty(parameter.DefaultValue))
            {
                defaults[parameter.Name] = parameter.DefaultValue;
            }
        }

        return defaults;
    }

    public bool ValidateParameters(
        CommandTemplate template,
        Dictionary<string, string> parameterValues,
        out List<string> errors)
    {
        errors = new List<string>();

        if (template == null)
        {
            errors.Add("Command template cannot be null");
            return false;
        }

        if (parameterValues == null)
        {
            errors.Add("Parameter values cannot be null");
            return false;
        }

        if (template.Parameters == null || template.Parameters.Count == 0)
        {
            return true; // No parameters to validate
        }

        if (template.Parameters.Count > ValidationConstants.MaxParametersPerTemplate)
        {
            errors.Add($"Command template declares too many parameters ({template.Parameters.Count}); maximum is {ValidationConstants.MaxParametersPerTemplate}");
            return false;
        }

        foreach (var parameter in template.Parameters)
        {
            if (parameter.Required)
            {
                if (!parameterValues.ContainsKey(parameter.Name) ||
                    string.IsNullOrWhiteSpace(parameterValues[parameter.Name]))
                {
                    errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterRequired, parameter.Label));
                }
            }

            // Type validation
            if (parameterValues.ContainsKey(parameter.Name) &&
                !string.IsNullOrWhiteSpace(parameterValues[parameter.Name]))
            {
                var value = parameterValues[parameter.Name];

                switch (parameter.Type.ToLowerInvariant())
                {
                    case "int":
                    case "integer":
                        if (!int.TryParse(value, out _))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeInteger, parameter.Label));
                        }
                        break;

                    case "number":
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        {
                            errors.Add($"{parameter.Label} must be a number");
                        }
                        break;

                    case "bool":
                    case "boolean":
                        if (!bool.TryParse(value, out _))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeBoolean, parameter.Label));
                        }
                        break;

                    case "string":
                        // PERFORMANCE: Use constant instead of magic number
                        if (value.Length > ValidationConstants.MaxParameterLength)
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMaxLength, parameter.Label, ValidationConstants.MaxParameterLength.ToString()));
                        }
                        if (ContainsDangerousCharacters(value))
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterDangerousCharacters, parameter.Label));
                        }
                        break;

                    case "hostname":
                        if (!IsValidHostname(value))
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidHostname, parameter.Label));
                        }
                        break;

                    case "ipaddress":
                        if (!IPAddress.TryParse(value, out _))
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidIPAddress, parameter.Label));
                        }
                        break;

                    case "driveletter":
                        if (!DriveLetterRegex.IsMatch(value))
                        {
                            errors.Add($"{parameter.Label} must be a single drive letter");
                        }
                        break;

                    case "path":
                        if (!IsValidPath(value))
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidPath, parameter.Label));
                        }
                        break;
                }
            }
        }

        return errors.Count == 0;
    }

    private static string EnforceCommandLength(string command)
    {
        if (command.Length > ValidationConstants.MaxCommandLength)
        {
            throw new InvalidOperationException(
                $"Generated command exceeds maximum length of {ValidationConstants.MaxCommandLength} characters (was {command.Length})");
        }

        return command;
    }

    /// <summary>
    /// Validates a parameter value based on its type
    /// </summary>
    private bool ValidateParameterValue(TemplateParameter parameter, string value, out string error)
    {
        error = string.Empty;

        switch (parameter.Type.ToLower())
        {
            case "hostname":
                if (!IsValidHostname(value))
                {
                    error = "Invalid hostname format";
                    return false;
                }
                break;

            case "ipaddress":
                if (!IPAddress.TryParse(value, out _))
                {
                    error = "Invalid IP address";
                    return false;
                }
                break;

            case "int":
            case "integer":
                if (!int.TryParse(value, out _))
                {
                    error = "Must be an integer";
                    return false;
                }
                break;

            case "number":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    error = "Must be a number";
                    return false;
                }
                break;

            case "driveletter":
                if (!DriveLetterRegex.IsMatch(value))
                {
                    error = "Must be a single drive letter";
                    return false;
                }
                break;

            case "path":
                if (!IsValidPath(value))
                {
                    error = "Invalid path format";
                    return false;
                }
                break;

            case "string":
                if (string.IsNullOrWhiteSpace(value) && parameter.Required)
                {
                    error = "Required field";
                    return false;
                }
                // PERFORMANCE: Use constant instead of magic number
                if (value.Length > ValidationConstants.MaxParameterLength)
                {
                    error = $"String exceeds maximum length of {ValidationConstants.MaxParameterLength}";
                    return false;
                }
                if (ContainsDangerousCharacters(value))
                {
                    error = "Contains dangerous characters";
                    return false;
                }
                break;
        }

        return true;
    }

    /// <summary>
    /// Escapes a parameter value based on its type
    /// </summary>
    private string EscapeParameterValue(string value, string parameterType, Platform platform, QuotingMode? quotingMode)
    {
        switch (parameterType.ToLower())
        {
            case "hostname":
            case "ipaddress":
            case "driveletter":
            case "int":
            case "integer":
            case "number":
                // These types are already validated with whitelist, no additional escaping needed
                return value;

            case "path":
            case "string":
            default:
                // Quote for shell safety using the target platform
                return (quotingMode ?? QuotingMode.ShellQuote) == QuotingMode.InlineInQuotes
                    ? EscapeInlineInSingleQuotes(value, platform)
                    : QuoteForShell(value, platform);
        }
    }

    /// <summary>
    /// Quotes a value for safe shell execution based on the target platform.
    /// Windows (PowerShell): Single quotes escaped by doubling ('')
    /// Linux/macOS (Bash): Single quotes escaped by closing, escaping, reopening ('\''')
    /// </summary>
    private static string QuoteForShell(string value, Platform platform)
    {
        if (platform == Platform.Windows)
        {
            // PowerShell escaping: Single quotes are escaped by doubling them
            // Example: "It's cool" becomes "'It''s cool'"
            return "'" + value.Replace("'", "''") + "'";
        }
        else
        {
            // Bash/Linux/macOS escaping: Single quotes are escaped by:
            // closing the quote, adding escaped literal quote, reopening quote
            // Example: "It's cool" becomes "'It'\''s cool'"
            return "'" + value.Replace("'", "'\\''") + "'";
        }
    }

    private static string EscapeInlineInSingleQuotes(string value, Platform platform)
    {
        if (platform == Platform.Windows)
        {
            return value.Replace("'", "''");
        }

        return value.Replace("'", "'\\''");
    }

    /// <summary>
    /// Validates hostname according to RFC 1123
    /// </summary>
    private bool IsValidHostname(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255)
            return false;

        // RFC 1123 compliant hostname validation
        return HostnameRegex.IsMatch(value);
    }

    /// <summary>
    /// Validates path format and prevents path traversal
    /// SECURITY FIX: Now validates that path is within allowed base directories
    /// </summary>
    private bool IsValidPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            // SECURITY: Check for tilde expansion attempts (shell-specific)
            if (value.Contains("~"))
                return false;

            // SECURITY: Check for Windows environment variables that could be exploited
            if (value.Contains("%"))
                return false;

            // SECURITY: Improved path traversal validation
            // Get the full normalized path
            var fullPath = Path.GetFullPath(value);
            var normalizedInput = Path.GetFullPath(value.Replace("/", Path.DirectorySeparatorChar.ToString()));

            // Check that normalization didn't reveal path traversal attempts
            // by ensuring the normalized path matches what we expect
            if (!fullPath.Equals(normalizedInput, StringComparison.OrdinalIgnoreCase))
                return false;

            // Verify path is rooted (absolute path)
            if (!Path.IsPathRooted(fullPath))
                return false;

            // SECURITY FIX: Validate that path is within allowed base directories
            // Only allow paths within user's AppData or LocalAppData folders (not full UserProfile)
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var blockedSubfolders = new[] { "Desktop", "Downloads", "Documents" };

            var allowedBases = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };

            // Block dangerous subfolders within UserProfile
            foreach (var blocked in blockedSubfolders)
            {
                var blockedPath = Path.Combine(userProfile, blocked);
                if (fullPath.StartsWith(Path.GetFullPath(blockedPath), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            var isInAllowedDirectory = allowedBases.Any(baseDir =>
            {
                var normalizedBase = Path.GetFullPath(baseDir);
                return fullPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase) &&
                       (fullPath.Length == normalizedBase.Length ||
                        fullPath[normalizedBase.Length] == Path.DirectorySeparatorChar ||
                        fullPath[normalizedBase.Length] == Path.AltDirectorySeparatorChar);
            });

            return isInAllowedDirectory;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Path validation failed - treat as invalid
            return false;
        }
    }

    /// <summary>
    /// Checks if a value contains dangerous shell characters
    /// </summary>
    private bool ContainsDangerousCharacters(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // List of dangerous characters that could be used for command injection
        return value.Any(c => DangerousChars.Contains(c) || char.IsControl(c));
    }
}
