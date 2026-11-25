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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// The format lock of version 1. Every file under <c>Recipes/FormatLock/v1</c> is a recipe that was valid (or
/// refused) when the format was frozen, with the text it must still parse to and the errors it must still give.
/// A sample may be added; it may never be edited or removed, because a file that loaded yesterday must load
/// tomorrow and mean the same thing. A change that makes one of these tests fail is a change of the format, and
/// the answer is a new schema version, never an edit of the sample.
/// </summary>
public sealed class RecipeFormatLockTests
{
    /// <summary>The fewest samples the freeze left in each folder; a scan that finds fewer did not reach them.</summary>
    private const int FewestAccepted = 35;

    private const int FewestRefused = 42;

    public static TheoryData<string> AcceptedSamples() => Samples(FormatLockSupport.AcceptedFolder);

    public static TheoryData<string> RefusedSamples() => Samples(FormatLockSupport.RefusedFolder);

    private static TheoryData<string> Samples(string folder)
    {
        TheoryData<string> data = [];
        foreach (string name in FormatLockSupport.SampleNames(folder))
            data.Add(name);

        return data;
    }

    private static string PathOf(string folder, string name, string suffix)
        => Path.Combine(FormatLockSupport.Root, folder, name + suffix);

    private static RecipeParseResult ParseSample(string folder, string name)
        => RecipeParser.Parse(File.ReadAllBytes(PathOf(folder, name, FormatLockSupport.RecipeSuffix)), FakeCharacterConverter.Plain);

    [Fact]
    public void TheScanReachesTheSubfoldersAndTheFilesOfTheFreeze()
    {
        Assert.True(Directory.Exists(FormatLockSupport.Root), FormatLockSupport.Root);
        Assert.True(FormatLockSupport.SampleNames(FormatLockSupport.AcceptedFolder).Count >= FewestAccepted);
        Assert.True(FormatLockSupport.SampleNames(FormatLockSupport.RefusedFolder).Count >= FewestRefused);
        Assert.True(File.Exists(Path.Combine(FormatLockSupport.Root, FormatLockSupport.ManifestFileName)));
    }

    [Theory]
    [MemberData(nameof(AcceptedSamples))]
    public void AnAcceptedSampleStillParsesWithoutAnError(string name)
    {
        RecipeParseResult result = ParseSample(FormatLockSupport.AcceptedFolder, name);

        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Code + "@" + error.Path)));
        Assert.Empty(result.Errors);
    }

    [Theory]
    [MemberData(nameof(AcceptedSamples))]
    public void AnAcceptedSampleStillMeansTheSameThingAndExpandsTheSameWay(string name)
    {
        Recipe recipe = ParseSample(FormatLockSupport.AcceptedFolder, name).Recipe!;
        string expected = FormatLockSupport.Normalize(File.ReadAllText(PathOf(FormatLockSupport.AcceptedFolder, name, FormatLockSupport.ExpectedSuffix)));

        string actual = FormatLockSupport.Compose(recipe, FormatLockSupport.ReadExpansions(expected));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(RefusedSamples))]
    public void ARefusedSampleStillGivesItsErrors(string name)
    {
        RecipeParseResult result = ParseSample(FormatLockSupport.RefusedFolder, name);
        string expected = FormatLockSupport.Normalize(File.ReadAllText(PathOf(FormatLockSupport.RefusedFolder, name, FormatLockSupport.ExpectedSuffix)));

        Assert.False(result.Succeeded);
        Assert.Equal(expected, FormatLockSupport.ComposeErrors(result.Errors));
    }

    [Fact]
    public void EverySampleHasItsExpectedFile()
    {
        foreach (string folder in new[] { FormatLockSupport.AcceptedFolder, FormatLockSupport.RefusedFolder })
        {
            foreach (string name in FormatLockSupport.SampleNames(folder))
                Assert.True(File.Exists(PathOf(folder, name, FormatLockSupport.ExpectedSuffix)), folder + "/" + name);
        }
    }

    [Fact]
    public void NoSampleWasEditedOrRemovedSinceTheFreeze()
    {
        string[] lines = File.ReadAllLines(Path.Combine(FormatLockSupport.Root, FormatLockSupport.ManifestFileName))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        Assert.NotEmpty(lines);

        foreach (string line in lines)
        {
            string[] parts = line.Split("  ", 2, StringSplitOptions.None);
            Assert.Equal(2, parts.Length);

            string path = Path.Combine(FormatLockSupport.Root, parts[1].Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "A frozen sample was removed: " + parts[1]);
            Assert.True(
                string.Equals(parts[0], FormatLockSupport.Hash(File.ReadAllBytes(path)), StringComparison.Ordinal),
                "A frozen sample was edited: " + parts[1]);
        }
    }

    [Fact]
    public void TheManifestListsEveryFileOfTheFreezeOnceAndTheFilesThatWereAddedLaterMayBeMissing()
    {
        string[] listed = [.. File.ReadAllLines(Path.Combine(FormatLockSupport.Root, FormatLockSupport.ManifestFileName))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Split("  ", 2, StringSplitOptions.None)[1])];

        Assert.Equal(listed.Length, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2 * (FewestAccepted + FewestRefused), listed.Length);
    }

    [Fact]
    public void AnEditedSampleWouldBeCaught()
    {
        byte[] original = File.ReadAllBytes(PathOf(FormatLockSupport.AcceptedFolder, FormatLockSupport.SampleNames(FormatLockSupport.AcceptedFolder)[0], FormatLockSupport.RecipeSuffix));
        byte[] edited = [.. original, (byte)' '];

        Assert.NotEqual(FormatLockSupport.Hash(original), FormatLockSupport.Hash(edited));
    }

    [Fact]
    public void TheHashDoesNotDependOnTheLineEndingsOfTheCheckout()
    {
        byte[] lf = "a\nb\n"u8.ToArray();
        byte[] crlf = "a\r\nb\r\n"u8.ToArray();

        Assert.Equal(FormatLockSupport.Hash(lf), FormatLockSupport.Hash(crlf));
    }

    [Fact]
    public void TheExampleOfTheDocumentationIsALockedSample()
    {
        string docs = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "TOOLS.md"));
        int heading = docs.IndexOf("### Writing a recipe", StringComparison.Ordinal);
        int open = docs.IndexOf("```json", heading, StringComparison.Ordinal);
        int close = docs.IndexOf("```", open + 7, StringComparison.Ordinal);
        string example = docs[(open + 7)..close];

        RecipeParseResult fromDocs = RecipeParser.Parse(example, FakeCharacterConverter.Plain);
        RecipeParseResult sample = ParseSample(FormatLockSupport.AcceptedFolder, "033-example-of-the-documentation-with-non-ascii-text");

        Assert.True(fromDocs.Succeeded, string.Join(", ", fromDocs.Errors.Select(error => error.Code + "@" + error.Path)));
        Assert.Equal(
            FormatLockSupport.Compose(sample.Recipe!, []),
            FormatLockSupport.Compose(fromDocs.Recipe!, []));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
