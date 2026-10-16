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

        return name => lookup.TryGetValue(name, out string? raw) ? Expand(raw) : null;
    }

    internal static string? Expand(string? raw)
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
}
