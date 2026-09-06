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

using System.IO;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Reads production source for the wiring tests that cannot construct the type they test, and
/// carries every presence anchor through <see cref="ViewSource.IsStatementOfTheMethodBody"/>.
/// </summary>
/// <remarks>
/// <para>A decision is pinned by its pure helper; what these readers pin is that the site which
/// owns the decision consults it. The source arrives with comments and literals blanked, so what
/// is read is code that runs rather than prose that resembles it, and an anchor is accepted only
/// as a statement of the block that encloses it: a call folded behind a term that is false by
/// construction keeps its text and loses that shape.</para>
/// <para>A chain of anchors reads nested sites: each anchor must be a statement of the block the
/// previous anchor opens. What no chain establishes is reachability; see the predicate's own
/// remark.</para>
/// </remarks>
internal static class SourceStatements
{
    /// <summary>The embedded SSH view, blanked.</summary>
    public static string ViewLogic() => Logic("src", "Heimdall.App", "Views", "EmbeddedSshView.xaml.cs");

    /// <summary>The SSH handler, blanked.</summary>
    public static string SshHandlerLogic() => Logic("src", "Heimdall.App", "Services", "Handlers", "SshHandler.cs");

    /// <summary>A production file with every comment and literal blanked.</summary>
    public static string Logic(params string[] relativePath) =>
        ViewSource.WithoutCommentsAndLiterals(
            File.ReadAllText(Path.Combine([ViewSource.RepoRoot(), .. relativePath])));

    /// <summary>One method of the blanked logic, from its signature to the next member.</summary>
    public static string Method(string logic, string signature) => ViewSource.HandlerBody(logic, signature);

    /// <summary>
    /// Asserts that each anchor is written as a statement of the block the previous anchor opens
    /// (the first anchor, of the method body itself) and returns the text from the last anchor on.
    /// </summary>
    public static string AssertStatementChain(string methodLogic, params string[] anchors)
    {
        ArgumentNullException.ThrowIfNull(methodLogic);
        ArgumentNullException.ThrowIfNull(anchors);

        string scope = methodLogic;
        foreach (string anchor in anchors)
        {
            Assert.True(
                ViewSource.IsStatementOfTheMethodBody(scope, anchor),
                $"'{anchor}' is not written as a step of the block that encloses it.");
            scope = scope[IndexOfStatement(scope, anchor)..];
        }

        return scope;
    }

    /// <summary>
    /// The offset of the occurrence the predicate accepted: at the block's own depth and starting
    /// a statement, so the slice that follows opens the block that occurrence opens.
    /// </summary>
    private static int IndexOfStatement(string scope, string anchor)
    {
        int bodyStart = scope.IndexOf('{');
        int depth = 0;
        for (int index = bodyStart; index < scope.Length; index++)
        {
            char current = scope[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
            }
            else if (depth == 1
                && string.CompareOrdinal(scope, index, anchor, 0, anchor.Length) == 0
                && StartsAStatement(scope, index))
            {
                return index;
            }
        }

        throw new InvalidOperationException($"No statement occurrence of '{anchor}'.");
    }

    private static bool StartsAStatement(string logic, int index)
    {
        for (int back = index - 1; back >= 0; back--)
        {
            char previous = logic[back];
            if (previous is '{' or '}' or ';')
            {
                return true;
            }

            if (!char.IsWhiteSpace(previous))
            {
                return false;
            }
        }

        return true;
    }
}
