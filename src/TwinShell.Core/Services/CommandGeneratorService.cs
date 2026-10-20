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
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using PowerShellSingleQuotedString = Heimdall.Core.Security.PowerShellSingleQuotedString;

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
            if (!ValidateParameterValue(template, parameter, value, out var validationError))
            {
                throw new InvalidOperationException($"Invalid parameter '{parameter.Name}': {validationError}");
            }

            // Escape the value for shell safety using the target platform
            var escapedValue = EscapeParameterValue(value, parameter.Type, template.Platform, parameter.Quoting);

            // Replace {parameterName} with actual value using StringBuilder
            // Built by the shared helper rather than spelled out here, so the substitution
            // and the checks that reason about placeholders cannot drift apart.
            var placeholder = CommandPatternQuoting.Placeholder(parameter.Name);
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

                // A fixed set is checked before the type switch, because "choice" carries
                // no shape of its own: what makes a value right is membership, not form.
                if (parameter.IsChoice && !IsOfferedValue(parameter, value))
                {
                    errors.Add(_localizationService.GetFormattedString(
                        MessageKeys.ValidationParameterNotAnOfferedValue,
                        parameter.Label,
                        string.Join(", ", parameter.AllowedValues!)));
                    continue;
                }

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
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeNumber, parameter.Label));
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
                        if (ContainsRefusedCharacters(template, parameter, value))
                        {
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
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeDriveLetter, parameter.Label));
                        }
                        break;

                    case "path":
                        if (!IsValidPath(value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidPath, parameter.Label));
                        }
                        else if (ContainsRefusedCharacters(template, parameter, value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterDangerousCharacters, parameter.Label));
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
    /// <summary>
    /// True when <paramref name="value"/> is one of the values a choice parameter offers.
    /// </summary>
    /// <remarks>
    /// Compared without regard to case or surrounding space, the way the offered values
    /// were typed by a person into an editor. An empty value passes: whether it is allowed
    /// to be empty is what <c>Required</c> decides, and two rules answering the same
    /// question is how they end up disagreeing.
    /// </remarks>
    private static bool IsOfferedValue(TemplateParameter parameter, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return parameter.AllowedValues!.Any(
            offered => string.Equals(offered?.Trim(), value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool ValidateParameterValue(CommandTemplate template, TemplateParameter parameter, string value, out string error)
    {
        error = string.Empty;

        if (parameter.IsChoice && !IsOfferedValue(parameter, value))
        {
            error = _localizationService.GetFormattedString(
                MessageKeys.ValidationParameterNotAnOfferedValue,
                parameter.Label,
                string.Join(", ", parameter.AllowedValues!));
            return false;
        }

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
                if (ContainsRefusedCharacters(template, parameter, value))
                {
                    error = "Contains dangerous characters";
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
                if (ContainsRefusedCharacters(template, parameter, value))
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
    /// Windows (PowerShell): every single quote PowerShell recognises, the apostrophe and four
    /// typographic ones, is escaped by doubling ('')
    /// Linux/macOS (Bash): Single quotes escaped by closing, escaping, reopening ('\''')
    /// </summary>
    private static string QuoteForShell(string value, Platform platform)
    {
        if (platform == Platform.Windows)
        {
            // Example: "It's cool" becomes "'It''s cool'"
            return PowerShellSingleQuotedString.Quote(value);
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
            return PowerShellSingleQuotedString.EscapeContent(value);
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
    /// True when <paramref name="value"/> has the shape of a path: not blank, no control
    /// character, and no longer than <see cref="ValidationConstants.MaxPathLength"/>.
    /// </summary>
    /// <remarks>
    /// The check is syntactic on purpose. The command runs on another machine, a Linux
    /// server or a remote Windows host, so resolving the value against this machine's file
    /// system said nothing about it: the old rule accepted only paths under the local
    /// AppData folders and refused /var/log/syslog and C:\inetpub. A path is quoted exactly
    /// like a string, so a tilde, a percent sign or a relative path is literal text.
    /// </remarks>
    private static bool IsValidPath(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length <= ValidationConstants.MaxPathLength
            && !value.Any(char.IsControl);
    }

    /// <summary>
    /// True when <paramref name="value"/> holds a character the generator will not put into
    /// <paramref name="template"/> for <paramref name="parameter"/>.
    /// </summary>
    /// <remarks>
    /// A control character is refused everywhere: a carriage return or a line feed submits
    /// the command on the terminal before the operator has read it, and no quoting stops
    /// that. The shell metacharacters are refused only where
    /// <see cref="RequiresCharacterBlacklist"/> says the quoting cannot neutralize them.
    /// </remarks>
    private static bool ContainsRefusedCharacters(CommandTemplate template, TemplateParameter parameter, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.Any(char.IsControl))
        {
            return true;
        }

        return RequiresCharacterBlacklist(template, parameter) && value.Any(c => DangerousChars.Contains(c));
    }

    /// <summary>
    /// True when a value for <paramref name="parameter"/> would reach the shell outside a
    /// literal single-quoted span, so shell metacharacters in it would be interpreted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one decision both validation paths read, so they cannot drift apart.
    /// </para>
    /// <para>
    /// <see cref="QuotingMode.ShellQuote"/> wraps the value in single quotes itself, which
    /// is literal only when the placeholder stands bare in the pattern: inside a
    /// double-quoted string the shell still expands <c>$(...)</c> between those quotes.
    /// <see cref="QuotingMode.InlineInQuotes"/> only escapes the value, so it is literal
    /// only when every occurrence of the placeholder sits in a single-quoted span of the
    /// pattern that no double-quoted string encloses. In both literal cases the only
    /// character that could end the span is a single quote, and both escapers double or
    /// close-and-reopen it, which is why the blacklist adds nothing there and refused
    /// everyday values such as LDAP filters, grep alternations and <c>$env:TEMP</c>.
    /// </para>
    /// </remarks>
    internal static bool RequiresCharacterBlacklist(CommandTemplate template, TemplateParameter parameter)
    {
        var literalContext = (parameter.Quoting ?? QuotingMode.ShellQuote) == QuotingMode.InlineInQuotes
            ? CommandPatternQuoting.QuoteContext.SingleQuoted
            : CommandPatternQuoting.QuoteContext.Bare;

        return CommandPatternQuoting
            .GetPlaceholderQuoteContexts(template.CommandPattern, parameter.Name, template.Platform == Platform.Windows)
            .Any(context => context != literalContext);
    }
}
