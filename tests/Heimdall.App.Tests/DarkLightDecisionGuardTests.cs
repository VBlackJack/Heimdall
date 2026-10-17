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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// One rule decides whether the current theme is dark: <c>ThemeLuminance</c>. The title bar,
/// the syntax palette, the diagram editor and the Markdown editor each had their own, two of
/// them testing the red channel alone, so a theme could be dark for one surface and light for
/// the one beside it.
/// </summary>
public sealed class DarkLightDecisionGuardTests
{
    // A darkness test on one colour channel, the shape the editors used: `Color.R < 128`.
    private static readonly Regex SingleChannelTest = new(@"\.Color\.[RGB]\s*<", RegexOptions.Compiled);

    [Fact]
    public void NoViewDecidesDarknessFromOneColourChannel()
    {
        string appRoot = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
        List<string> found = [];
        bool reachedTools = false;

        foreach (string file in SourceFileEnumeration.EnumerateFiles(appRoot, "*.cs"))
        {
            reachedTools |= file.Contains(Path.Combine("Views", "Tools"), StringComparison.OrdinalIgnoreCase);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (SingleChannelTest.IsMatch(lines[i]))
                {
                    found.Add($"{Path.GetRelativePath(appRoot, file)}:{i + 1}");
                }
            }
        }

        Assert.True(reachedTools, "the scan never opened Views/Tools");
        Assert.True(found.Count == 0, "Ask ThemeLuminance instead:" + Environment.NewLine + string.Join(Environment.NewLine, found));
    }
}
