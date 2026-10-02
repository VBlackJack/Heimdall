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

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// Launching the real executable on a developer machine runs it against the developer's own
/// profile, so those tests run only when the run opts in.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class ProductLaunchFactAttributeTests
{
    // Split so this file does not read as a launcher itself.
    private const string LaunchCall = "Application" + ".Launch(";
    private const string OptInAttribute = "[ProductLaunchFact]";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData(" 1")]
    public void IsOptedIn_AnythingButTheOptInValue_IsNotAnOptIn(string? value)
    {
        Assert.False(ProductLaunchFactAttribute.IsOptedIn(value));
    }

    [Fact]
    public void IsOptedIn_TheOptInValue_IsAnOptIn()
    {
        Assert.True(ProductLaunchFactAttribute.IsOptedIn(ProductLaunchFactAttribute.OptInValue));
    }

    [Fact]
    public void Attribute_WithoutTheOptIn_SkipsTheTest()
    {
        string? previous = Environment.GetEnvironmentVariable(ProductLaunchFactAttribute.OptInEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(ProductLaunchFactAttribute.OptInEnvironmentVariable, null);
            Assert.False(string.IsNullOrWhiteSpace(new ProductLaunchFactAttribute().Skip));

            Environment.SetEnvironmentVariable(
                ProductLaunchFactAttribute.OptInEnvironmentVariable, ProductLaunchFactAttribute.OptInValue);
            Assert.Null(new ProductLaunchFactAttribute().Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProductLaunchFactAttribute.OptInEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// A test file that launches the executable must take the opt-in, and only the opt-in:
    /// a plain fact beside it would launch the product on every developer run.
    /// </summary>
    [Fact]
    public void EveryFileThatLaunchesTheProduct_UsesOnlyTheOptInFact()
    {
        string testsRoot = Path.Combine(WpfTestHost.RepoRoot, "tests", "Heimdall.App.UiTests");
        string[] launchers = Directory.GetFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains(LaunchCall, StringComparison.Ordinal))
            .ToArray();

        // The guard must reach the one launcher that exists, or it guards nothing.
        Assert.Contains(launchers, path => Path.GetFileName(path) == "ShellLaunchTests.cs");

        foreach (string path in launchers)
        {
            string source = File.ReadAllText(path);
            Assert.True(source.Contains(OptInAttribute, StringComparison.Ordinal), $"{path} launches the product without {OptInAttribute}.");
            Assert.DoesNotContain("[StaFact]", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[Fact]", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[Theory]", source, StringComparison.Ordinal);
        }
    }
}
