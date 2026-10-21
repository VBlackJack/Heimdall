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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// The one piece of templating a runbook has: a step's parameter value may name a runbook
/// variable as <c>{{name}}</c>, and that reference is replaced by the variable's value before
/// the step's command is generated.
/// </summary>
/// <remarks>
/// Nothing else is interpreted: no expressions, no defaults inside the braces, no escaping. A
/// reference to a name the runbook does not declare is left exactly as written and reported, so
/// the step shows an error instead of sending text the operator did not mean.
/// </remarks>
public static partial class RunbookVariables
{
    /// <summary>
    /// A variable name: a letter or an underscore, then letters, digits or underscores.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();

    /// <summary>True when <paramref name="name"/> can be declared as a variable.</summary>
    public static bool IsValidName(string? name)
        => !string.IsNullOrEmpty(name) && NamePattern().IsMatch(name);

    /// <summary>
    /// Replaces every <c>{{name}}</c> in <paramref name="value"/> with the value of the variable
    /// of that name, and adds to <paramref name="unknown"/> each referenced name with no variable.
    /// </summary>
    public static string Substitute(
        string? value,
        IReadOnlyDictionary<string, string> variables,
        ISet<string> unknown)
    {
        ArgumentNullException.ThrowIfNull(variables);
        ArgumentNullException.ThrowIfNull(unknown);

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return ReferencePattern().Replace(value, match =>
        {
            var name = match.Groups[1].Value;
            if (variables.TryGetValue(name, out var replacement))
            {
                return replacement ?? string.Empty;
            }

            unknown.Add(name);
            return match.Value;
        });
    }

    /// <summary>
    /// The parameter values of a step with every variable reference substituted.
    /// </summary>
    public static Dictionary<string, string> Resolve(
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyDictionary<string, string> variables,
        ISet<string> unknown)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters is null)
        {
            return resolved;
        }

        foreach (var (name, value) in parameters)
        {
            resolved[name] = Substitute(value, variables, unknown);
        }

        return resolved;
    }

    /// <summary>The text a reference to <paramref name="name"/> is written as.</summary>
    public static string FormatReference(string name) => "{{" + name + "}}";
}
