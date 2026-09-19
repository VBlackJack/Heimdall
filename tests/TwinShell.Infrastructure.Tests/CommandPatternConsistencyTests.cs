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

using TwinShell.Core.Helpers;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Pins the two ways a command pattern and its declared parameters drift apart.
/// </summary>
/// <remarks>
/// The generator is declaration-driven: it walks the declared parameters and replaces
/// each one's placeholder, and never looks at the pattern for anything else. So a
/// placeholder nobody declared is left in the command verbatim, and a parameter the
/// pattern never mentions is a box filled to no effect. Neither says anything today.
/// </remarks>
public sealed class CommandPatternConsistencyTests
{
    // ── Undeclared placeholders ───────────────────────────────────

    [Fact]
    public void APlaceholderNobodyDeclaredIsReported()
    {
        var found = CommandPatternQuoting.FindUndeclaredPlaceholders(
            "ssh -p {port} {host}", ["host"]);

        Assert.Equal(["port"], found);
    }

    [Fact]
    public void ADeclaredPlaceholderIsNotReported()
    {
        var found = CommandPatternQuoting.FindUndeclaredPlaceholders(
            "tail -n {lines} {path}", ["lines", "path"]);

        Assert.Empty(found);
    }

    [Fact]
    public void TheSamePlaceholderTwiceIsReportedOnce()
    {
        var found = CommandPatternQuoting.FindUndeclaredPlaceholders(
            "cp {path} {path}.bak", []);

        Assert.Equal(["path"], found);
    }

    /// <summary>
    /// Braces are ordinary shell punctuation, and this is the whole reason the rule is
    /// written as narrowly as it is.
    /// </summary>
    /// <remarks>
    /// Every one of these is a command somebody really writes. A check that flagged them
    /// would cry wolf on the patterns most worth having, and would be switched off.
    /// </remarks>
    [Theory]
    [InlineData("awk '{print $1}' /var/log/syslog")]
    [InlineData("jq '{name: .user}' data.json")]
    [InlineData("cp file.{txt,bak}")]
    [InlineData("echo {1..10}")]
    [InlineData("grep -E 'a{2,3}' file")]
    [InlineData("find . -name '*.tmp' -exec rm {} \\;")]
    [InlineData("echo \"${HOME}/bin\"")]
    [InlineData("sed 's/x/y/' <<< \"${VAR}\"")]
    public void OrdinaryShellBracesAreNotMistakenForPlaceholders(string pattern)
    {
        Assert.Empty(CommandPatternQuoting.FindUndeclaredPlaceholders(pattern, []));
    }

    /// <summary>
    /// The dollar-sign rule must not swallow a real placeholder that merely follows one.
    /// </summary>
    [Fact]
    public void APlaceholderNextToAShellVariableIsStillSeen()
    {
        var found = CommandPatternQuoting.FindUndeclaredPlaceholders(
            "cp \"${HOME}/{name}\" /tmp", []);

        Assert.Equal(["name"], found);
    }

    // ── Declared but unused ───────────────────────────────────────

    [Fact]
    public void AParameterThePatternNeverMentionsIsReported()
    {
        var found = CommandPatternQuoting.FindUnusedParameters(
            "tail -f {path}", ["path", "lines"]);

        Assert.Equal(["lines"], found);
    }

    [Fact]
    public void AParameterThePatternUsesIsNotReported()
    {
        Assert.Empty(CommandPatternQuoting.FindUnusedParameters("tail -f {path}", ["path"]));
    }

    /// <summary>
    /// A name is matched whole, inside braces. A parameter called "path" is not satisfied
    /// by the word "path" appearing in the command.
    /// </summary>
    [Fact]
    public void ANameMentionedWithoutBracesDoesNotCount()
    {
        var found = CommandPatternQuoting.FindUnusedParameters(
            "echo path is not a placeholder", ["path"]);

        Assert.Equal(["path"], found);
    }

    /// <summary>
    /// A name is matched exactly: "{path}" does not satisfy a parameter called "pat".
    /// </summary>
    [Fact]
    public void APrefixOfAPlaceholderDoesNotCount()
    {
        var found = CommandPatternQuoting.FindUnusedParameters("tail -f {path}", ["pat"]);

        Assert.Equal(["pat"], found);
    }

    [Fact]
    public void ABlankParameterNameIsReported()
    {
        var found = CommandPatternQuoting.FindUnusedParameters("tail -f {path}", ["path", "  "]);

        Assert.Equal(["  "], found);
    }

    // ── The shared placeholder builder ────────────────────────────

    /// <summary>
    /// The generator substitutes what this returns, so the checks above only mean
    /// something while they agree with it.
    /// </summary>
    [Fact]
    public void ThePlaceholderBuilderWrapsTheNameInBraces()
    {
        Assert.Equal("{path}", CommandPatternQuoting.Placeholder("path"));
    }

    /// <summary>
    /// Ties the three users of the placeholder together: what the builder produces is what
    /// the unused-parameter check looks for, and what the quoting predicate locates.
    /// </summary>
    [Fact]
    public void TheBuilderAgreesWithBothCheckAndQuotingPredicate()
    {
        var pattern = "mysql -p'" + CommandPatternQuoting.Placeholder("password") + "'";

        Assert.Empty(CommandPatternQuoting.FindUnusedParameters(pattern, ["password"]));
        Assert.True(CommandPatternQuoting.IsPlaceholderInsideSingleQuotedSpan(pattern, "password"));
    }

    // ── Degenerate input ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnAbsentPatternReportsNoPlaceholdersAndEveryParameterUnused(string? pattern)
    {
        Assert.Empty(CommandPatternQuoting.FindUndeclaredPlaceholders(pattern, ["path"]));
        Assert.Equal(["path"], CommandPatternQuoting.FindUnusedParameters(pattern, ["path"]));
    }
}
