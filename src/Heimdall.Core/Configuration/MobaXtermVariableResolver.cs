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

using System.Text.RegularExpressions;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Builds the resolver a MobaXterm parse uses for its <c>[NAME]</c> variables, from the
/// user-defined values in <see cref="AppSettings.MobaXtermUserVariables"/>.
/// </summary>
public static partial class MobaXtermVariableResolver
{
    /// <summary>
    /// Returns a resolver that looks a variable name up case-insensitively and expands
    /// environment variables in its value.
    /// </summary>
    /// <remarks>
    /// A value that expands to nothing, or that still names an environment variable the system
    /// does not define, resolves to null: the parse then keeps the token and reports it, rather
    /// than importing a login such as <c>%USERNAME%</c> spelled out literally.
    /// </remarks>
    public static Func<string, string?> FromVariables(IReadOnlyDictionary<string, string>? variables)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (variables is not null)
        {
            foreach (KeyValuePair<string, string> pair in variables)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    lookup[pair.Key.Trim()] = pair.Value;
                }
            }
        }

        return name => lookup.TryGetValue(name, out string? raw) ? ResolveValue(raw) : null;
    }

    /// <summary>
    /// Whether <paramref name="name"/> can name a variable: letters, digits and underscores, not
    /// starting with a digit, which is the token body the MobaXterm parser recognises inside
    /// <c>[...]</c>. A name outside it could be saved but would never be replaced.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && VariableNameRegex().IsMatch(name);

    /// <summary>
    /// The value a variable resolves to: <paramref name="raw"/> with environment variables
    /// expanded, or null when that leaves nothing or an undefined <c>%NAME%</c>.
    /// </summary>
    /// <remarks>
    /// The one place a value is resolved, for the import and for the settings preview alike, so
    /// the two cannot disagree.
    /// </remarks>
    public static string? ResolveValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(raw).Trim();
        if (expanded.Length == 0 || UnexpandedReferenceRegex().IsMatch(expanded))
        {
            return null;
        }

        return expanded;
    }

    [GeneratedRegex(@"%[^%\s]+%")]
    private static partial Regex UnexpandedReferenceRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableNameRegex();
}
