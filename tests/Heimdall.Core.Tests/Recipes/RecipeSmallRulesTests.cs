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

/// <summary>Leading white space, the pattern text check, drive roots and hand-edited folder lists.</summary>
public sealed class RecipeSmallRulesTests
{
    private static IRecipeCharacterConverter Plain => FakeCharacterConverter.Plain;

    private static RecipeInput Text(string name = "v")
        => RecipeTestData.Input(name, RecipeInputType.String, required: true) with { MaxLength = RecipeLimits.MaxStringValueLength };

    // ---- leading white space

    [Theory]
    [InlineData(" x")]
    [InlineData("  x")]
    public void ALeadingSpace_IsRefusedWhereThePlaceholderStartsTheToken(string value)
    {
        Assert.Equal(RecipeValueRefusal.SurroundingWhitespace, RecipeValueRule.Check(Text(), value, placeholderStartsToken: true, Plain));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(Text(), value, placeholderStartsToken: false, Plain));
    }

    [Fact]
    public void ALeadingSpace_IsRefusedByTheExpander_AtTheStartOfATokenOnly()
    {
        Recipe atStart = RecipeTestData.With([Text()], RecipeArgument.Bare("{in:v}"));
        Recipe inside = RecipeTestData.With([Text()], RecipeArgument.Bare("--v={in:v}"));
        Dictionary<string, string?> values = new() { ["v"] = " x" };

        RecipeExpansion refused = RecipeExpander.Expand(atStart, values, null, null, Plain);
        RecipeExpansion accepted = RecipeExpander.Expand(inside, values, null, null, Plain);

        Assert.Equal(RecipeValueRefusal.SurroundingWhitespace, Assert.Single(refused.Refusals).Reason);
        Assert.Equal(["--v= x"], accepted.Arguments);
    }

    [Fact]
    public void ATrailingSpace_IsStillAcceptedInAStringValue()
        => Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(Text(), "x ", placeholderStartsToken: true, Plain));

    // ---- the pattern is recipe text like any other

    [Fact]
    public void APatternWithAControlCharacter_IsRefusedAsText()
    {
        RecipeInput input = Text() with { Pattern = "a\nb" };

        IReadOnlyList<RecipeError> errors = RecipeValidator.Validate(RecipeTestData.With([input], RecipeArgument.Group(["--v", "{in:v}"])), Plain);

        RecipeError error = Assert.Single(errors);
        Assert.Equal(RecipeErrorCode.ControlCharacter, error.Code);
        Assert.Equal("inputs[0].pattern", error.Path);
    }

    [Fact]
    public void APatternWithAnUnpairedSurrogate_IsRefusedAsText()
    {
        RecipeInput input = Text() with { Pattern = new string(['a', '\uD800']) };

        Assert.Contains(
            RecipeErrorCode.ControlCharacter,
            RecipeValidator.Validate(RecipeTestData.With([input], RecipeArgument.Group(["--v", "{in:v}"])), Plain).Select(error => error.Code));
    }

    [Theory]
    [InlineData(@"[a-z]+\d*")]
    [InlineData(@"\\server")]
    [InlineData("caf\u00E9")]
    public void AnOrdinaryPattern_IsStillAcceptedWhateverItsBackslashes(string pattern)
    {
        RecipeInput input = Text() with { Pattern = pattern };

        Assert.Empty(RecipeValidator.Validate(RecipeTestData.With([input], RecipeArgument.Group(["--v", "{in:v}"])), Plain));
    }

    [Fact]
    public void AnInvalidPattern_IsStillRefusedAsAPattern()
    {
        RecipeInput input = Text() with { Pattern = "(" };

        Assert.Equal(
            RecipeErrorCode.InvalidPattern,
            Assert.Single(RecipeValidator.Validate(RecipeTestData.With([input], RecipeArgument.Group(["--v", "{in:v}"])), Plain)).Code);
    }

    // ---- folders the user adds

    [Theory]
    [InlineData(@"D:\")]
    [InlineData("D:/")]
    [InlineData(@"d:\\")]
    public void TheRootOfADrive_IsRefusedAsAProgramFolder(string folder)
        => Assert.Equal(RecipeFolderRefusal.DriveRoot, RecipeFolderRule.Check(folder));

    [Fact]
    public void AFolderOnADrive_IsStillAcceptedAndTheDriveRootStillIsNotInTheListCheck()
    {
        Assert.Equal(RecipeFolderRefusal.None, RecipeFolderRule.Check(@"D:\Tools"));
        Assert.Equal(RecipeFolderRefusal.DriveRoot, RecipeFolderRule.Check(@"D:\", ["E:\\Tools"]));
    }

    [Theory]
    [InlineData(@"D:\tools\..\other")]
    [InlineData(@"D:\tools\.\sub")]
    [InlineData(@"D:\other\..")]
    [InlineData(@"D:\")]
    [InlineData("D:")]
    [InlineData(@"D:\to%TEMP%ols")]
    [InlineData(@"\\server\share")]
    public void AHandEditedFolderThatBreaksTheRule_IsIgnoredAndOpensNothing(string folder)
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"D:\other\x.exe").WithFile(@"D:\x.exe");
        RecipeProgramResolver resolver = new(machine);

        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, resolver.Resolve(@"D:\other\x.exe", [folder]).Refusal);
        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, resolver.Resolve(@"D:\x.exe", [folder]).Refusal);
    }

    [Fact]
    public void AValidFolderBesideAnIgnoredOne_StillOpensItsOwnRoot()
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"D:\other\x.exe");
        RecipeProgramResolver resolver = new(machine);

        Assert.True(resolver.Resolve(@"D:\other\x.exe", [@"D:\tools\..\elsewhere", @"D:\other"]).IsResolved);
    }
}
