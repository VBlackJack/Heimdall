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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// Every button in the application names the style it is drawn with.
/// </summary>
/// <remarks>
/// <para>There is no implicit <c>Button</c> style in this theme and there never has been: a button
/// that names none is drawn by WPF's own default template, a pale grey slab that ignores the theme
/// entirely. Nothing fails when that happens. The build is clean, every test is green, and the
/// button is simply the wrong colour until somebody looks at the window.</para>
/// <para>Eight shipped that way before this guard existed, seven of them added in a single
/// afternoon.</para>
/// </remarks>
public sealed class ButtonStyleGuardTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Well under the seven hundred the application carries, and far above what a broken sweep
    /// would find. An assertion over an empty set passes, and a guard that walked the wrong
    /// directory would read as success.
    /// </summary>
    private const int MinimumButtonsSeen = 500;

    [Fact]
    public void EveryButtonNamesTheStyleItIsDrawnWith()
    {
        int seen = 0;
        List<string> unstyled = [];

        foreach (string path in MarkupFiles())
        {
            XDocument document = XDocument.Load(path);
            foreach (XElement button in document.Descendants(Presentation + "Button"))
            {
                seen++;
                if (IsDrawnWithANamedStyle(button))
                {
                    continue;
                }

                unstyled.Add(
                    $"{Path.GetFileName(path)}: "
                    + (button.Attribute(Xaml + "Name")?.Value
                        ?? button.Attribute("Content")?.Value
                        ?? "(unnamed)"));
            }
        }

        Assert.True(
            seen >= MinimumButtonsSeen,
            $"only {seen} buttons were read, so this guard measured almost nothing");

        Assert.True(
            unstyled.Count == 0,
            "these buttons would be drawn by WPF's default template, which ignores the theme:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, unstyled));
    }

    /// <summary>
    /// A button is drawn with a style it names: as an attribute, as a <c>Button.Style</c> or
    /// <c>Button.Template</c> child, or through an implicit style an ancestor declares in its own
    /// resources.
    /// </summary>
    /// <remarks>
    /// The three ways matter. A sweep that reads the opening tag alone calls four buttons of
    /// <c>ServerDialog</c> unstyled when each carries a <c>Button.Style</c> child, and a count
    /// taken that way says fifteen where the truth is one.
    /// </remarks>
    private static bool IsDrawnWithANamedStyle(XElement button)
    {
        if (button.Attribute("Style") is not null)
        {
            return true;
        }

        if (button.Elements().Any(child =>
            child.Name.LocalName is "Button.Style" or "Button.Template"))
        {
            return true;
        }

        return button.Ancestors().Any(DeclaresAnImplicitButtonStyle);
    }

    private static bool DeclaresAnImplicitButtonStyle(XElement element) =>
        element.Elements()
            .Where(child => child.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal))
            .SelectMany(resources => resources.Descendants())
            .Any(resource => resource.Name.LocalName == "Style"
                && resource.Attribute(Xaml + "Key") is null
                && resource.Attribute("TargetType")?.Value.Contains("Button", StringComparison.Ordinal) == true);

    private static IEnumerable<string> MarkupFiles()
    {
        string root = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");

        return Directory
            .EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
}
