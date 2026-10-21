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
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The spacing scale the views actually use is 2 4 8 12 16 20 24. The tokens stopped at
/// 4 8 12 20 24, so the two most common literal steps outside them, 2 and 16, had no name and
/// were written as numbers.
/// </summary>
public sealed class SpacingTokenScaleTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("SpacingXxs", "2")]
    [InlineData("SpacingXs", "4")]
    [InlineData("SpacingSm", "8")]
    [InlineData("SpacingMd", "12")]
    [InlineData("SpacingMl", "16")]
    [InlineData("SpacingLg", "20")]
    [InlineData("SpacingXl", "24")]
    public void EachStepOfTheScaleIsAUniformThicknessToken(string key, string value)
    {
        XDocument controls = XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Themes", "CommonControls.xaml"));

        XElement token = Assert.Single(
            controls.Descendants(),
            element => (string?)element.Attribute(Xaml + "Key") == key);

        Assert.Equal("Thickness", token.Name.LocalName);
        Assert.Equal(value, token.Value.Trim());
    }
}
