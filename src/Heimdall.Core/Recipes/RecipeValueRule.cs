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

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Heimdall.Core.Security;

namespace Heimdall.Core.Recipes;

/// <summary>Why a value was refused at run time. <see cref="None"/> means it was accepted.</summary>
public enum RecipeValueRefusal
{
    /// <summary>The value is accepted.</summary>
    None,

    /// <summary>The value is empty where a value was needed.</summary>
    Empty,

    /// <summary>A required input has no value and no default.</summary>
    RequiredMissing,

    /// <summary>The selected session offers no host or no port for a placeholder that needs one.</summary>
    SessionValueMissing,

    /// <summary>The value holds a control, format, separator or unpaired surrogate character.</summary>
    ControlCharacter,

    /// <summary>
    /// The value holds a character that a program reading its command line in the ANSI code
    /// page would be handed as an ASCII one (a full-width hyphen, a typographic quote, a no-break space).
    /// </summary>
    LookAlikeCharacter,

    /// <summary>The value starts with <c>-</c> where its placeholder starts the token.</summary>
    LeadingDash,

    /// <summary>The value starts with <c>/</c> where its placeholder starts the token.</summary>
    LeadingSlash,

    /// <summary>The value starts with <c>@</c> where its placeholder starts the token.</summary>
    LeadingAt,

    /// <summary>The value starts with a backslash where its placeholder starts the token.</summary>
    LeadingBackslash,

    /// <summary>The path is a UNC path.</summary>
    UncPath,

    /// <summary>The path is a device path (<c>\\?\</c> or <c>\\.\</c>).</summary>
    DevicePath,

    /// <summary>The path holds a colon other than the one that ends a drive letter.</summary>
    ColonInPath,

    /// <summary>The path, or one of its parts, ends with a dot or a space.</summary>
    TrailingDotOrSpace,

    /// <summary>A part of the path is a DOS device name.</summary>
    DeviceName,

    /// <summary>The value has trailing white space, or leading white space where its placeholder starts the token, where none is allowed.</summary>
    SurroundingWhitespace,

    /// <summary>The value is not an integer.</summary>
    NotAnInteger,

    /// <summary>The value is not a finite number.</summary>
    NotANumber,

    /// <summary>The value is not <c>true</c> or <c>false</c>.</summary>
    NotABoolean,

    /// <summary>The path starts with a single separator: rooted but without a drive.</summary>
    RootedPath,

    /// <summary>
    /// The value would join the literal or value next to it into a doubled separator (a UNC or
    /// URL shape) that is not in the reviewed file.
    /// </summary>
    SeparatorJoin,

    /// <summary>
    /// A final argument holds two separators in a row (either form, in any mix), the NT prefix or a
    /// URL scheme marker, whatever its source.
    /// </summary>
    ForbiddenSequenceInArgument,

    /// <summary>The value is not a valid host name.</summary>
    InvalidHostname,

    /// <summary>The value is not a valid IP address.</summary>
    InvalidIpAddress,

    /// <summary>The value is not a valid port number.</summary>
    InvalidPort,

    /// <summary>The value is longer than the field allows.</summary>
    TooLong,

    /// <summary>The value does not match the field's pattern (or the pattern timed out).</summary>
    PatternMismatch,

    /// <summary>The value is not one of the field's choices.</summary>
    NotInChoices,
}

/// <summary>
/// The rule every substituted value must pass before it becomes part of a process argument.
/// A recipe's arguments are never quoted or joined, so the rule is about what a program might read
/// as an option, a response file, a network path or a device, not about shell syntax.
/// </summary>
public static class RecipeValueRule
{
    private const int DriveColonIndex = 1;

    private const int RootedPrefixLength = 2;

    private const int MaxDeviceNameDigit = 9;

    private static readonly string[] DeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
    ];

    private static readonly string[] NumberedDeviceNames = ["COM", "LPT"];

    private const string SuperscriptDigits = "\u00B9\u00B2\u00B3";

    /// <summary>
    /// Checks a value for a recipe input.
    /// </summary>
    /// <param name="field">The input the value is for.</param>
    /// <param name="candidate">The value exactly as it would be substituted.</param>
    /// <param name="placeholderStartsToken">True when the placeholder is the very first thing of its token.</param>
    /// <param name="converter">What the machine does to a character; the real machine when null.</param>
    public static RecipeValueRefusal Check(
        RecipeInput field,
        string? candidate,
        bool placeholderStartsToken,
        IRecipeCharacterConverter? converter = null)
    {
        ArgumentNullException.ThrowIfNull(field);

        RecipeValueRefusal common = CheckCommon(candidate, placeholderStartsToken, converter);
        if (common != RecipeValueRefusal.None)
            return common;

        string value = candidate!;
        return field.Type switch
        {
            RecipeInputType.String => CheckString(field, value),
            RecipeInputType.Int => CheckInteger(value),
            RecipeInputType.Number => CheckNumber(value),
            RecipeInputType.Bool => CheckBool(value),
            RecipeInputType.Hostname => CheckHostname(value),
            RecipeInputType.IpAddress => CheckIpAddress(value),
            RecipeInputType.Path => CheckPath(value),
            RecipeInputType.Choice => CheckChoice(field, value),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field.Type, "Unknown recipe input type."),
        };
    }

    /// <summary>
    /// Checks the value of the <c>{Host}</c> placeholder with the strictest host validation of
    /// <see cref="InputValidator"/> (the DNS-checked <c>Hostname</c> pattern).
    /// </summary>
    public static RecipeValueRefusal CheckHost(
        string? candidate,
        bool placeholderStartsToken,
        IRecipeCharacterConverter? converter = null)
    {
        RecipeValueRefusal common = CheckCommon(candidate, placeholderStartsToken, converter);
        return common != RecipeValueRefusal.None ? common : CheckHostname(candidate!);
    }

    /// <summary>
    /// Checks the value of the <c>{Port}</c> placeholder: ASCII digits only, inside the range
    /// <see cref="InputValidator.ValidatePortRange"/> accepts, and matching its <c>Port</c> pattern.
    /// </summary>
    public static RecipeValueRefusal CheckPort(
        string? candidate,
        bool placeholderStartsToken,
        IRecipeCharacterConverter? converter = null)
    {
        RecipeValueRefusal common = CheckCommon(candidate, placeholderStartsToken, converter);
        if (common != RecipeValueRefusal.None)
            return common;

        string value = candidate!;
        bool ok = int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && InputValidator.ValidatePortRange(port)
            && InputValidator.Validate(value, "Port");
        return ok ? RecipeValueRefusal.None : RecipeValueRefusal.InvalidPort;
    }

    /// <summary>
    /// Refuses a value that would create a doubled separator with its neighbours: it starts with
    /// <c>\</c> or <c>/</c> right after a separator, or ends with one right before a separator.
    /// </summary>
    /// <param name="value">The substituted value.</param>
    /// <param name="charBefore">The last character of the text just before it in the token, or null.</param>
    /// <param name="charAfter">The first character of the text just after it in the token, or null.</param>
    public static RecipeValueRefusal CheckSeparatorJoin(string value, char? charBefore, char? charAfter)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            return RecipeValueRefusal.None;

        bool before = charBefore is char b && IsSeparator(b) && IsSeparator(value[0]);
        bool after = charAfter is char a && IsSeparator(a) && IsSeparator(value[^1]);
        return before || after ? RecipeValueRefusal.SeparatorJoin : RecipeValueRefusal.None;
    }

    private static RecipeValueRefusal CheckCommon(string? candidate, bool placeholderStartsToken, IRecipeCharacterConverter? converter)
    {
        if (string.IsNullOrEmpty(candidate))
            return RecipeValueRefusal.Empty;

        if (RecipeText.ContainsRefusedCharacter(candidate))
            return RecipeValueRefusal.ControlCharacter;

        if (RecipeText.ContainsLookAlikeCharacter(candidate, converter))
            return RecipeValueRefusal.LookAlikeCharacter;

        if (placeholderStartsToken)
        {
            // A leading space is dropped or kept depending on the program, and a leading option
            // character, backslash or at sign changes what the whole argument means.
            if (char.IsWhiteSpace(candidate[0]))
                return RecipeValueRefusal.SurroundingWhitespace;

            switch (candidate[0])
            {
                case '-':
                    return RecipeValueRefusal.LeadingDash;
                case '/':
                    return RecipeValueRefusal.LeadingSlash;
                case '@':
                    return RecipeValueRefusal.LeadingAt;
                case '\\':
                    return RecipeValueRefusal.LeadingBackslash;
            }
        }

        return RecipeValueRefusal.None;
    }

    private static RecipeValueRefusal CheckString(RecipeInput field, string value)
    {
        int cap = field.MaxLength ?? RecipeLimits.DefaultStringMaxLength;
        if (value.Length > cap)
            return RecipeValueRefusal.TooLong;

        if (field.Pattern is null)
            return RecipeValueRefusal.None;

        try
        {
            return RecipePatterns.IsFullMatch(field.Pattern, value)
                ? RecipeValueRefusal.None
                : RecipeValueRefusal.PatternMismatch;
        }
        catch (RegexMatchTimeoutException)
        {
            return RecipeValueRefusal.PatternMismatch;
        }
    }

    private static RecipeValueRefusal CheckInteger(string value)
    {
        bool ok = value.Length <= RecipeLimits.MaxNumericValueLength
            && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        return ok ? RecipeValueRefusal.None : RecipeValueRefusal.NotAnInteger;
    }

    private static RecipeValueRefusal CheckNumber(string value)
    {
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        bool ok = value.Length <= RecipeLimits.MaxNumericValueLength
            && double.TryParse(value, styles, CultureInfo.InvariantCulture, out double number)
            && double.IsFinite(number);
        return ok ? RecipeValueRefusal.None : RecipeValueRefusal.NotANumber;
    }

    private static RecipeValueRefusal CheckBool(string value)
    {
        bool ok = string.Equals(value, RecipeFormat.TrueText, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, RecipeFormat.FalseText, StringComparison.OrdinalIgnoreCase);
        return ok ? RecipeValueRefusal.None : RecipeValueRefusal.NotABoolean;
    }

    private static RecipeValueRefusal CheckHostname(string value)
    {
        // InputValidator trims before it matches, and the untrimmed value is what would be passed on.
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return RecipeValueRefusal.SurroundingWhitespace;

        return InputValidator.Validate(value, "Hostname")
            ? RecipeValueRefusal.None
            : RecipeValueRefusal.InvalidHostname;
    }

    private static RecipeValueRefusal CheckIpAddress(string value)
    {
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            return RecipeValueRefusal.SurroundingWhitespace;

        if (value.Contains(':', StringComparison.Ordinal))
        {
            // IPv6: hexadecimal digits, colons and an optional dotted tail; no zone id ("%"), no brackets.
            foreach (char c in value)
            {
                bool allowed = c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F') or ':' or '.';
                if (!allowed)
                    return RecipeValueRefusal.InvalidIpAddress;
            }

            return IPAddress.TryParse(value, out _) ? RecipeValueRefusal.None : RecipeValueRefusal.InvalidIpAddress;
        }

        return InputValidator.Validate(value, "IPv4") ? RecipeValueRefusal.None : RecipeValueRefusal.InvalidIpAddress;
    }

    private static RecipeValueRefusal CheckChoice(RecipeInput field, string value)
    {
        if (field.Choices is null)
            return RecipeValueRefusal.NotInChoices;

        foreach (string choice in field.Choices)
        {
            if (string.Equals(choice, value, StringComparison.Ordinal))
                return RecipeValueRefusal.None;
        }

        return RecipeValueRefusal.NotInChoices;
    }

    private static RecipeValueRefusal CheckPath(string value)
    {
        if (RecipeText.HasNtPrefix(value) && IsSeparator(value[0]) && value[1] == '?')
            return RecipeValueRefusal.DevicePath;

        if (IsSeparator(value[0]) && value.Length >= RootedPrefixLength && IsSeparator(value[1]))
        {
            int marker = RootedPrefixLength;
            bool device = value.Length > marker + 1
                && (value[marker] == '?' || value[marker] == '.')
                && IsSeparator(value[marker + 1]);
            return device ? RecipeValueRefusal.DevicePath : RecipeValueRefusal.UncPath;
        }

        if (IsSeparator(value[0]))
            return RecipeValueRefusal.RootedPath;

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != ':')
                continue;

            // The only colon allowed is the one right after a drive letter, followed by a separator
            // or nothing ("C:" and "C:foo" are drive-relative, which depends on a hidden folder).
            bool driveColon = index == DriveColonIndex
                && char.IsAsciiLetter(value[0])
                && (value.Length == RootedPrefixLength || IsSeparator(value[RootedPrefixLength]));
            if (!driveColon)
                return RecipeValueRefusal.ColonInPath;
        }

        if (value[^1] is '.' or ' ')
            return RecipeValueRefusal.TrailingDotOrSpace;

        foreach (string part in value.Split('\\', '/'))
        {
            if (part.Length == 0 || part is "." or "..")
                continue;

            if (part[^1] is '.' or ' ')
                return RecipeValueRefusal.TrailingDotOrSpace;

            if (IsDeviceName(part))
                return RecipeValueRefusal.DeviceName;
        }

        return RecipeValueRefusal.None;
    }

    private static bool IsSeparator(char c) => RecipeText.IsSeparator(c);

    private static bool IsDeviceName(string part)
    {
        int dot = part.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? part : part[..dot]).TrimEnd(' ');

        foreach (string name in DeviceNames)
        {
            if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (string prefix in NumberedDeviceNames)
        {
            if (stem.Length == prefix.Length + 1
                && stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && IsDeviceDigit(stem[^1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDeviceDigit(char c)
        => c is >= '1' and <= (char)('0' + MaxDeviceNameDigit) || SuperscriptDigits.Contains(c, StringComparison.Ordinal);
}
