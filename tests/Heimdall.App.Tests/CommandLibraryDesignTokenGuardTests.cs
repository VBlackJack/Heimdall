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
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// Keeps the Command Library and its runbook editor on the design tokens they were moved to.
/// </summary>
/// <remarks>
/// <para>
/// Both files had <c>FontFamily="Consolas"</c> written out beside a theme that defines
/// <c>FontFamilyMonospace</c>, and spacing values that duplicated theme tokens. They were moved to
/// the tokens; a hard-coded family or a spacing value equal to a token is what this refuses.
/// </para>
/// <para>
/// Scoped to these two files on purpose: other views still carry literals, and a guard over all
/// of them would be red on arrival.
/// </para>
/// </remarks>
public sealed class CommandLibraryDesignTokenGuardTests
{
    private static readonly string[] GuardedFiles =
    [
        Path.Combine("src", "Heimdall.App", "Views", "Tools", "CommandLibraryView.xaml"),
        Path.Combine("src", "Heimdall.App", "Views", "Dialogs", "RunbookDialog.xaml")
    ];

    private static readonly Regex LiteralFontFamily = new(
        @"\bFontFamily=""(?!\{)[^""]+""", RegexOptions.CultureInvariant);

    // Spacing values the theme already names (MarginInlineSm, MarginInlineXs, MarginInlineControl,
    // MarginLabelBottom, MarginDetailRow, MarginSubsectionTop, SpacingXs/Sm/Md).
    private static readonly Regex TokenSpacingLiteral = new(
        @"\b(Margin|Padding)=""(0,0,8,0|0,0,4,0|8,0,0,0|0,0,0,4|0,0,0,6|0,8,0,0|4|8|12)""",
        RegexOptions.CultureInvariant);

    [Theory]
    [MemberData(nameof(Files))]
    public void NoFontFamilyIsWrittenOut(string relativePath)
        => Assert.Empty(Matches(LiteralFontFamily, relativePath));

    [Theory]
    [MemberData(nameof(Files))]
    public void NoSpacingRepeatsAThemeToken(string relativePath)
        => Assert.Empty(Matches(TokenSpacingLiteral, relativePath));

    /// <summary>
    /// The control: the patterns match the markup they are meant to refuse, so an empty result
    /// above is a clean file and not a pattern that matches nothing.
    /// </summary>
    [Fact]
    public void ThePatternsCatchTheLiteralsTheyRefuse()
    {
        Assert.Matches(LiteralFontFamily, @"<TextBox FontFamily=""Consolas""/>");
        Assert.DoesNotMatch(LiteralFontFamily, @"<TextBox FontFamily=""{StaticResource FontFamilyMonospace}""/>");
        Assert.Matches(TokenSpacingLiteral, @"<Border Padding=""4""/>");
        Assert.Matches(TokenSpacingLiteral, @"<Button Margin=""0,0,8,0""/>");
        Assert.DoesNotMatch(TokenSpacingLiteral, @"<Border Padding=""4,1""/>");
    }

    public static TheoryData<string> Files() => new(GuardedFiles);

    private static List<string> Matches(Regex pattern, string relativePath)
    {
        string path = Path.Combine(FindRepoRoot(), relativePath);
        Assert.True(File.Exists(path), $"Guarded file not found: {relativePath}");
        return pattern.Matches(File.ReadAllText(path)).Select(m => m.Value).ToList();
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
