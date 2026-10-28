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
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.Core.Localization;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The names that a session's host and user fill are one list, the library and the palette both
/// read it, and the runbook variables help spells it out literally in every language.
/// </summary>
public sealed partial class SessionPrefillNamesTests
{
    private const string HelpKey = "ToolCmdLibRunbookVariablesHelp";
    private const string TargetHost = "srv-01.example.test";
    private const string UnrelatedName = "path";

    [GeneratedRegex(@"\{\{([^{}]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static TheoryData<string> HostNames => ToTheoryData(SessionPrefillNames.HostNames);

    public static TheoryData<string> UserNames => ToTheoryData(SessionPrefillNames.UserNames);

    [Theory]
    [MemberData(nameof(HostNames))]
    public void EveryHostName_IsAHost_InTheLibraryAndThePalette(string name)
    {
        Assert.True(SessionPrefillNames.IsHost(name, type: null));
        Assert.True(CommandLibraryViewModel.IsHostParameter(name, type: null));
        Assert.True(CommandLibraryViewModel.IsHostParameter(name.ToUpperInvariant(), type: null));
        Assert.Equal(TargetHost, PalettePrefill(name));
    }

    [Theory]
    [MemberData(nameof(UserNames))]
    public void EveryUserName_IsAUser_InTheLibrary(string name)
    {
        Assert.True(SessionPrefillNames.IsUser(name));
        Assert.True(CommandLibraryViewModel.IsUserParameter(name));
        Assert.True(CommandLibraryViewModel.IsUserParameter(name.ToUpperInvariant()));
    }

    [Fact]
    public void AnUnrelatedName_IsNeitherAHostNorAUser()
    {
        Assert.False(SessionPrefillNames.IsHost(UnrelatedName, type: null));
        Assert.False(SessionPrefillNames.IsUser(UnrelatedName));
        Assert.False(CommandLibraryViewModel.IsHostParameter(UnrelatedName, type: null));
        Assert.Equal(string.Empty, PalettePrefill(UnrelatedName));
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("ipaddress")]
    [InlineData("HostName")]
    public void AHostTypedParameter_IsAHostWhateverItIsCalled(string type)
    {
        Assert.True(SessionPrefillNames.IsHost(UnrelatedName, type));
        Assert.True(CommandLibraryViewModel.IsHostParameter(UnrelatedName, type));
    }

    [Fact]
    public void TheTwoListsDoNotOverlap()
    {
        Assert.Empty(SessionPrefillNames.HostNames.Intersect(SessionPrefillNames.UserNames));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheHelp_NamesEveryPrefilledVariableLiterally(string language)
    {
        string help = await HelpAsync(language);
        HashSet<string> tokens = Tokens(help);

        foreach (string name in SessionPrefillNames.HostNames.Concat(SessionPrefillNames.UserNames))
        {
            Assert.True(
                tokens.Contains(name),
                $"The {language} help does not name {{{{{name}}}}}, which the session prefills.");
        }
    }

    [Fact]
    public async Task TheHelp_WritesTheSameLiteralTokensInEveryLanguage()
    {
        // Only the first token, the example of a name the operator picks, is translated. Every
        // other token is a name the code matches exactly, so a translated one would promise a
        // prefill that never happens.
        HashSet<string> prefilled = [.. SessionPrefillNames.HostNames, .. SessionPrefillNames.UserNames];

        foreach (string language in new[] { "en", "fr", "es" })
        {
            HashSet<string> tokens = Tokens(await HelpAsync(language));
            HashSet<string> literal = [.. tokens.Where(prefilled.Contains)];

            Assert.True(
                literal.SetEquals(prefilled),
                $"The {language} help differs from the prefilled names: "
                    + string.Join(", ", prefilled.Except(literal)));
            Assert.Equal(prefilled.Count + 1, TokenPattern().Matches(await HelpAsync(language)).Count);
        }
    }

    private static string PalettePrefill(string parameterName)
    {
        CommandTemplate template = new()
        {
            Name = "template",
            CommandPattern = "do {x}",
            Parameters = [new TemplateParameter { Name = parameterName, Type = "string" }],
        };

        return SnippetParameterFactory.Build(template, TargetHost).Single().Value;
    }

    private static HashSet<string> Tokens(string help)
        => [.. TokenPattern().Matches(help).Select(match => match.Groups[1].Value)];

    private static async Task<string> HelpAsync(string language)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);
        return localizer.GetString(HelpKey);
    }

    private static TheoryData<string> ToTheoryData(IReadOnlyList<string> names)
    {
        TheoryData<string> data = [];
        foreach (string name in names)
        {
            data.Add(name);
        }

        return data;
    }
}
