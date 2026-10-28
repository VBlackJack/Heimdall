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

    // Every pattern below ends with \z, not $: $ also matches before a final line feed, and a
    // value that passes here goes into the command bare, so a trailing line feed would
    // submit the line.
    // BUGFIX: Declare Regex as static readonly for better performance
    private static readonly Regex HostnameRegex = new Regex(@"^(?!-)([a-zA-Z0-9-]{1,63}(?<!-)\.)*[a-zA-Z0-9-]{1,63}\z", RegexOptions.Compiled);

    private static readonly Regex DriveLetterRegex = new Regex(@"^[A-Za-z]\z", RegexOptions.Compiled);

    // A value made only of these characters reads the same to bash and PowerShell with or
    // without quotes; see IsSafeBareValue.
    private static readonly Regex SafeBareValueRegex = new Regex(@"^[A-Za-z0-9._/:=@+,%-]+\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
            throw new InvalidOperationException(TooManyParametersMessage(template.Parameters.Count));
        }

        // What each declared placeholder becomes: the escaped value, or null when an optional
        // parameter was left empty and its argument is to be dropped. Every value is resolved
        // first and the pattern is walked once afterwards (see SubstitutePlaceholders), so a
        // value typed by the operator is never read as pattern text.
        var substitutions = new Dictionary<string, string?>(StringComparer.Ordinal);

        // The flag each optional parameter declares for itself (TemplateParameter.Flag): when
        // the parameter is dropped, so is that flag.
        var droppedFlags = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var parameter in template.Parameters)
        {
            if (parameter == null)
            {
                continue; // Skip null parameters
            }

            var value = parameterValues.ContainsKey(parameter.Name)
                ? parameterValues[parameter.Name]
                : parameter.DefaultValue ?? string.Empty;

            // Built by the shared helper rather than spelled out here, so the substitution
            // and the checks that reason about placeholders cannot drift apart.
            var placeholder = CommandPatternQuoting.Placeholder(parameter.Name);

            if (string.IsNullOrWhiteSpace(value))
            {
                if (parameter.Required)
                {
                    throw new InvalidOperationException(
                        _localizationService.GetFormattedString(MessageKeys.ValidationParameterRequired, parameter.Label));
                }

                // An optional value left empty means "leave the argument out": quoting it
                // would hand the command an empty argument instead (hostname '').
                substitutions.TryAdd(placeholder, null);
                if (!string.IsNullOrEmpty(parameter.Flag))
                {
                    droppedFlags.TryAdd(placeholder, parameter.Flag);
                }

                continue;
            }

            // Validate the value according to the parameter type. The message already names
            // the parameter, in the operator's language.
            if (!ValidateParameterValue(template, parameter, value, out var validationError))
            {
                throw new InvalidOperationException(validationError);
            }

            // Escape the value for shell safety using the target platform. A value goes in
            // bare only where every occurrence stands outside quotes and after no dollar sign:
            // after $ the shell would read it as a variable name, and inside a quoted span the
            // pattern's own quoting decides.
            var everyOccurrenceBare = CommandPatternQuoting
                .GetPlaceholderQuoteContexts(template.CommandPattern, parameter.Name, template.Platform == Platform.Windows)
                .All(context => context == CommandPatternQuoting.QuoteContext.Bare);
            var escapedValue = EscapeParameterValue(value, parameter.Type, template.Platform, parameter.Quoting, everyOccurrenceBare);

            substitutions.TryAdd(placeholder, escapedValue);
        }

        return EnforceCommandLength(SubstitutePlaceholders(template.CommandPattern, substitutions, droppedFlags));
    }

    /// <summary>
    /// Builds the command in one pass over <paramref name="pattern"/>: pattern text is copied,
    /// each declared placeholder is replaced by its entry in <paramref name="substitutions"/>,
    /// and an entry of <see langword="null"/> drops the placeholder together with the run of
    /// spaces it leaves, and with the flag <paramref name="droppedFlags"/> names for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A value is never scanned again. Replacing one parameter after another over the whole
    /// command let a value that spelled another parameter's placeholder (<c>{b}</c> typed
    /// into <c>{a}</c>) be rewritten later with the quoting of the wrong position, so
    /// <c>echo {a} {b}</c> with a=<c>{b}</c> and b=<c>x; id</c> ran <c>id</c>. Dropping an
    /// optional placeholder likewise could no longer reach into a value's text.
    /// </para>
    /// <para>
    /// Only the spaces next to a dropped placeholder are touched: two spaces written into
    /// the pattern elsewhere, inside a quoted grep expression for instance, are the
    /// author's and stay. A placeholder that ends the command takes the space before it
    /// along, so <c>hostname {name}</c> becomes <c>hostname</c>. A space that ends an
    /// inserted value belongs to the value and is left alone.
    /// </para>
    /// <para>
    /// A flag is dropped only when it is exactly the token written just before the placeholder
    /// in the pattern: <c>-Title {title}</c> loses <c>-Title</c>, while a flag that is not
    /// there, or is glued to the text before it or to a value inserted earlier, leaves the
    /// command as it was. The pattern is the author's, so a wrong declaration costs a
    /// dangling flag, never a removed word.
    /// </para>
    /// </remarks>
    internal static string SubstitutePlaceholders(
        string pattern,
        IReadOnlyDictionary<string, string?> substitutions,
        IReadOnlyDictionary<string, string>? droppedFlags = null)
    {
        var result = new StringBuilder(pattern.Length);

        // Everything before this length came from an inserted value: the space handling
        // around a dropped placeholder looks only at pattern text written after it.
        var valueEnd = 0;
        var closeBrace = -1;
        var noMoreClosingBrace = false;
        var index = 0;

        while (index < pattern.Length)
        {
            var current = pattern[index];

            if (current == '{')
            {
                if (closeBrace < index && !noMoreClosingBrace)
                {
                    closeBrace = pattern.IndexOf('}', index);
                    noMoreClosingBrace = closeBrace < 0;
                }

                var placeholderText = closeBrace > index ? pattern.Substring(index, closeBrace - index + 1) : null;
                if (placeholderText is not null
                    && substitutions.TryGetValue(placeholderText, out var replacement))
                {
                    index = closeBrace + 1;

                    if (replacement is not null)
                    {
                        result.Append(replacement);
                        valueEnd = result.Length;
                        continue;
                    }

                    if (droppedFlags is not null && droppedFlags.TryGetValue(placeholderText, out var flag))
                    {
                        DropTrailingFlag(result, valueEnd, flag);
                    }

                    if (result.Length == 0 || (result.Length > valueEnd && result[^1] == ' '))
                    {
                        while (index < pattern.Length && pattern[index] == ' ')
                        {
                            index++;
                        }
                    }

                    if (index >= pattern.Length)
                    {
                        while (result.Length > valueEnd && result[^1] == ' ')
                        {
                            result.Length--;
                        }
                    }

                    continue;
                }
            }

            result.Append(current);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Removes <paramref name="flag"/> from the end of <paramref name="result"/>, with the
    /// spaces between it and the placeholder being dropped, when it is the last token of the
    /// pattern text written after <paramref name="valueEnd"/>.
    /// </summary>
    private static void DropTrailingFlag(StringBuilder result, int valueEnd, string flag)
    {
        var end = result.Length;
        while (end > valueEnd && result[end - 1] == ' ')
        {
            end--;
        }

        var start = end - flag.Length;
        if (flag.Length == 0 || start < valueEnd)
        {
            return;
        }

        for (var offset = 0; offset < flag.Length; offset++)
        {
            if (result[start + offset] != flag[offset])
            {
                return;
            }
        }

        // A token starts the command or follows a space; a flag glued to a value or to other
        // text is part of something else.
        var startsToken = start == 0 || (start > valueEnd && result[start - 1] == ' ');
        if (startsToken)
        {
            result.Length = start;
        }
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
            errors.Add(TooManyParametersMessage(template.Parameters.Count));
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
                    case TemplateParameterTypes.Int:
                    case TemplateParameterTypes.Integer:
                        if (!IsInteger(value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeInteger, parameter.Label));
                        }
                        break;

                    case TemplateParameterTypes.Number:
                        if (!IsNumber(value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeNumber, parameter.Label));
                        }
                        break;

                    case TemplateParameterTypes.Bool:
                    case TemplateParameterTypes.Boolean:
                        if (!bool.TryParse(value, out _))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeBoolean, parameter.Label));
                        }
                        break;

                    case TemplateParameterTypes.Text:
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

                    case TemplateParameterTypes.Hostname:
                        if (!IsValidHostname(value))
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidHostname, parameter.Label));
                        }
                        break;

                    case TemplateParameterTypes.IpAddress:
                        if (!IPAddress.TryParse(value, out _))
                        {
                            // BUGFIX: Replace hardcoded French message with localization
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidIPAddress, parameter.Label));
                        }
                        break;

                    case TemplateParameterTypes.DriveLetter:
                        if (!DriveLetterRegex.IsMatch(value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterMustBeDriveLetter, parameter.Label));
                        }
                        break;

                    case TemplateParameterTypes.Path:
                        if (!IsValidPath(value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterInvalidPath, parameter.Label));
                        }
                        else if (ContainsRefusedCharacters(template, parameter, value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterDangerousCharacters, parameter.Label));
                        }
                        break;

                    default:
                        // A type this switch does not know (a choice, or a name typed by
                        // hand into an imported file) is still quoted like a string, so it
                        // gets at least the checks no quoting replaces.
                        if (ContainsRefusedCharacters(template, parameter, value))
                        {
                            errors.Add(_localizationService.GetFormattedString(MessageKeys.ValidationParameterDangerousCharacters, parameter.Label));
                        }
                        break;
                }
            }
        }

        return errors.Count == 0;
    }

    private string EnforceCommandLength(string command)
    {
        if (command.Length > ValidationConstants.MaxCommandLength)
        {
            throw new InvalidOperationException(_localizationService.GetFormattedString(
                MessageKeys.ValidationParameterCommandTooLong,
                command.Length.ToString(CultureInfo.InvariantCulture),
                ValidationConstants.MaxCommandLength.ToString(CultureInfo.InvariantCulture)));
        }

        return command;
    }

    /// <summary>
    /// The refusal of a template that declares more than
    /// <see cref="ValidationConstants.MaxParametersPerTemplate"/> parameters, in the operator's
    /// language: it reaches them through the same error line as every other refusal.
    /// </summary>
    private string TooManyParametersMessage(int count) =>
        _localizationService.GetFormattedString(
            MessageKeys.ValidationParameterCountTooHigh,
            count.ToString(CultureInfo.InvariantCulture),
            ValidationConstants.MaxParametersPerTemplate.ToString(CultureInfo.InvariantCulture));

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

        // The same keys ValidateParameters reports, so a refusal reads the same in the
        // operator's language whichever path met it first.
        string? key = null;
        switch (parameter.Type.ToLowerInvariant())
        {
            case TemplateParameterTypes.Hostname:
                if (!IsValidHostname(value))
                {
                    key = MessageKeys.ValidationParameterInvalidHostname;
                }
                break;

            case TemplateParameterTypes.IpAddress:
                if (!IPAddress.TryParse(value, out _))
                {
                    key = MessageKeys.ValidationParameterInvalidIPAddress;
                }
                break;

            case TemplateParameterTypes.Int:
            case TemplateParameterTypes.Integer:
                if (!IsInteger(value))
                {
                    key = MessageKeys.ValidationParameterMustBeInteger;
                }
                break;

            case TemplateParameterTypes.Number:
                if (!IsNumber(value))
                {
                    key = MessageKeys.ValidationParameterMustBeNumber;
                }
                break;

            case TemplateParameterTypes.Bool:
            case TemplateParameterTypes.Boolean:
                if (!bool.TryParse(value, out _))
                {
                    key = MessageKeys.ValidationParameterMustBeBoolean;
                }
                break;

            case TemplateParameterTypes.DriveLetter:
                if (!DriveLetterRegex.IsMatch(value))
                {
                    key = MessageKeys.ValidationParameterMustBeDriveLetter;
                }
                break;

            case TemplateParameterTypes.Path:
                if (!IsValidPath(value))
                {
                    key = MessageKeys.ValidationParameterInvalidPath;
                }
                else if (ContainsRefusedCharacters(template, parameter, value))
                {
                    key = MessageKeys.ValidationParameterDangerousCharacters;
                }
                break;

            case TemplateParameterTypes.Text:
                if (string.IsNullOrWhiteSpace(value) && parameter.Required)
                {
                    key = MessageKeys.ValidationParameterRequired;
                }
                else if (value.Length > ValidationConstants.MaxParameterLength)
                {
                    error = _localizationService.GetFormattedString(
                        MessageKeys.ValidationParameterMaxLength,
                        parameter.Label,
                        ValidationConstants.MaxParameterLength.ToString(CultureInfo.InvariantCulture));
                    return false;
                }
                else if (ContainsRefusedCharacters(template, parameter, value))
                {
                    key = MessageKeys.ValidationParameterDangerousCharacters;
                }
                break;

            default:
                // Same rule as ValidateParameters: an unknown type is quoted like a string,
                // so it gets at least the checks no quoting replaces.
                if (ContainsRefusedCharacters(template, parameter, value))
                {
                    key = MessageKeys.ValidationParameterDangerousCharacters;
                }
                break;
        }

        if (key is not null)
        {
            error = _localizationService.GetFormattedString(key, parameter.Label);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Escapes a parameter value based on its type
    /// </summary>
    private string EscapeParameterValue(string value, string parameterType, Platform platform, QuotingMode? quotingMode, bool everyOccurrenceBare)
    {
        switch (parameterType.ToLowerInvariant())
        {
            case TemplateParameterTypes.Hostname:
            case TemplateParameterTypes.IpAddress:
            case TemplateParameterTypes.DriveLetter:
            case TemplateParameterTypes.Int:
            case TemplateParameterTypes.Integer:
            case TemplateParameterTypes.Number:
                // These types are already validated with whitelist, no additional escaping needed
                return value;

            case TemplateParameterTypes.Path:
            case TemplateParameterTypes.Text:
            default:
                if ((quotingMode ?? QuotingMode.ShellQuote) == QuotingMode.InlineInQuotes)
                {
                    // The quotes are the pattern's: only the content is escaped.
                    return EscapeInlineInSingleQuotes(value, platform);
                }

                return everyOccurrenceBare && IsSafeBareValue(value, platform) ? value : QuoteForShell(value, platform);
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> means the same to the shell with or without
    /// quotes, so it can be substituted bare: <c>--add-port=8080/tcp</c> rather than
    /// <c>--add-port='8080'/'tcp'</c>, <c>~/.ssh/id_ed25519.pub</c> rather than
    /// <c>~/.ssh/'id_ed25519'.pub</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The class holds no space, no quote, no glob character and no <c>$</c>, so no shell
    /// splits, expands or substitutes anything in such a value.
    /// </para>
    /// <para>
    /// PowerShell reads three of its characters differently at the start of an argument or
    /// between words, so on Windows those values keep their quotes: a leading <c>-</c> makes
    /// a parameter name of the value, a leading <c>@</c> splats a variable, and a comma
    /// builds an array of two arguments out of one. Bash gives none of them a meaning.
    /// </para>
    /// </remarks>
    internal static bool IsSafeBareValue(string value, Platform platform)
    {
        if (!SafeBareValueRegex.IsMatch(value))
        {
            return false;
        }

        return platform != Platform.Windows
            || (value[0] is not ('-' or '@') && !value.Contains(','));
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
    /// True when <paramref name="value"/> is a whole number: an optional sign and digits,
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// The value goes into the command bare, so the parse must not be more generous than the
    /// shell: <see cref="NumberStyles.Integer"/> would also accept leading and trailing
    /// white space, a trailing line feed among it. The current culture is not consulted
    /// either, since the operator's regional settings say nothing about the target shell.
    /// </remarks>
    private static bool IsInteger(string value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// True when <paramref name="value"/> is a decimal number: an optional sign, digits, an
    /// optional decimal point and an optional exponent, with no surrounding white space.
    /// </summary>
    /// <remarks>
    /// <see cref="NumberStyles.Float"/> allows leading and trailing white space, which
    /// would reach the command bare; see <see cref="IsInteger"/>.
    /// </remarks>
    private static bool IsNumber(string value) =>
        double.TryParse(
            value,
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
            CultureInfo.InvariantCulture,
            out _);

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
