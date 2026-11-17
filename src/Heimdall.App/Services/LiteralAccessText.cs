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
using System.Text;
using System.Windows.Automation;

namespace Heimdall.App.Services;

/// <summary>
/// Shows text that is data, not a label, exactly as it is written in a control that reads access keys.
/// </summary>
/// <remarks>
/// <para>
/// The themed menu item, button, check box, radio button, tab item and combo box item draw a string
/// through an access-key parser (Heimdall's own styles and WPF's default templates alike): the first underscore
/// is swallowed and the letter after it is underlined and bound to Alt. That is wanted for a
/// localized label that carries a key on purpose, and wrong for a bookmark, a folder, a macro, a
/// tool or a host, whose name is whatever its owner typed or the remote side sent: a path
/// <c>/etc/my_app.conf</c> read <c>/etc/myapp.conf</c>. The parser reads a doubled underscore as
/// one literal underscore, so the data goes in with each underscore doubled.
/// </para>
/// <para>
/// The escaped form is for the header only. Assistive technology must hear the real text, so the
/// helpers that set a header also set the automation name to the text as it was given.
/// </para>
/// </remarks>
internal static class LiteralAccessText
{
    private const string AccessKeyMarker = "_";

    private const string LiteralAccessKeyMarker = "__";

    /// <summary>The text with each underscore doubled, so a menu item header shows it literally.</summary>
    /// <param name="text">The data to show; null reads as empty.</param>
    /// <returns>The escaped text, for a header and for nothing else.</returns>
    public static string Literal(string? text)
    {
        return string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace(AccessKeyMarker, LiteralAccessKeyMarker, StringComparison.Ordinal);
    }

    /// <summary>Gives the item a header that shows the text literally and an automation name that is the text.</summary>
    /// <param name="item">The menu item to fill.</param>
    /// <param name="text">The data to show; null reads as empty.</param>
    public static void SetLiteral(MenuItem item, string? text)
    {
        ArgumentNullException.ThrowIfNull(item);

        item.Header = Literal(text);
        AutomationProperties.SetName(item, text ?? string.Empty);
    }

    /// <summary>
    /// Gives the item a header made of a localized template and data: the data is escaped, the
    /// template is left alone so a key it carries on purpose still works.
    /// </summary>
    /// <param name="item">The menu item to fill.</param>
    /// <param name="template">The localized composite format.</param>
    /// <param name="arguments">The values of the template; text among them is data.</param>
    public static void SetFormatted(MenuItem item, string template, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(arguments);

        object?[] escaped = [.. arguments.Select(argument => argument is string text ? Literal(text) : argument)];
        item.Header = string.Format(CultureInfo.CurrentCulture, template, escaped);
        AutomationProperties.SetName(item, FormatName(template, arguments));
    }

    /// <summary>
    /// The text a screen reader should hear for a localized template filled with real data: the
    /// template read the way the access-key parser reads it, the data left as it is.
    /// </summary>
    /// <param name="template">The localized composite format, which may carry an access key.</param>
    /// <param name="arguments">The unescaped values of the template.</param>
    public static string FormatName(string template, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(arguments);

        return string.Format(CultureInfo.CurrentCulture, RemoveAccessKeyMarker(template), arguments);
    }

    /// <summary>
    /// The text as the access-key parser shows it: the first single underscore, the one that marks
    /// the key, is dropped, and each doubled underscore reads as one.
    /// </summary>
    /// <param name="text">A label that may carry an access key.</param>
    /// <returns>What the label displays, and what assistive technology should hear.</returns>
    public static string RemoveAccessKeyMarker(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder shown = new(text.Length);
        bool markerRemoved = false;
        for (int index = 0; index < text.Length; index++)
        {
            char current = text[index];
            bool isLast = index == text.Length - 1;
            if (current != AccessKeyMarker[0] || isLast)
            {
                shown.Append(current);
            }
            else if (text[index + 1] == AccessKeyMarker[0])
            {
                shown.Append(current);
                index++;
            }
            else if (markerRemoved)
            {
                shown.Append(current);
            }
            else
            {
                markerRemoved = true;
            }
        }

        return shown.ToString();
    }

    /// <summary>Names an item whose header was escaped by the caller after the text it stands for.</summary>
    /// <param name="item">The menu item to name.</param>
    /// <param name="text">The text as it was before escaping.</param>
    public static void SetAutomationName(MenuItem item, string text)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(text);

        AutomationProperties.SetName(item, text);
    }
}
