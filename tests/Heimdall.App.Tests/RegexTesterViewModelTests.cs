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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Localization;
using Heimdall.Core.Matching;

namespace Heimdall.App.Tests;

public sealed class RegexTesterViewModelTests
{
    [Fact]
    public void EmptyPattern_ShowsEmptyState()
    {
        var vm = CreateViewModel();
        vm.Initialize(null);
        vm.MarkInitialized();

        vm.PatternText = string.Empty;
        vm.FlushPendingMatch();

        Assert.True(vm.IsEmptyStateVisible);
        Assert.False(vm.IsResultsPanelVisible);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void ValidPattern_EmptyTest_ShowsValidStatusWithoutResults()
    {
        var vm = CreateViewModel(new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.Success, 0, [], string.Empty),
        });
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.PatternText = "a";
        vm.TestText = string.Empty;
        vm.FlushPendingMatch();

        Assert.Equal("ToolRegexStatusValid", vm.StatusText);
        Assert.False(vm.IsEmptyStateVisible);
        Assert.False(vm.IsResultsPanelVisible);
    }

    [Fact]
    public async Task ValidPattern_EmptyTest_LocalizedStatusAfterInitialize()
    {
        var vm = CreateViewModel(new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.Success, 0, [], string.Empty),
        });
        var localizer = await CreateLocalizerAsync("en");
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.PatternText = "a";
        vm.FlushPendingMatch();

        Assert.Equal("Valid regex", vm.StatusText);
    }

    [Fact]
    public async Task InvalidPattern_ShowsErrorStatus()
    {
        var vm = CreateViewModel(new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.InvalidPattern, 0, [], "bad pattern"),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "[";
        vm.TestText = "abc";
        vm.FlushPendingMatch();

        Assert.Equal("Invalid regex: bad pattern", vm.StatusText);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
        Assert.False(vm.IsResultsPanelVisible);
    }

    [Fact]
    public async Task Timeout_ShowsTimeoutStatus()
    {
        var vm = CreateViewModel(new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.MatchTimeout, 0, [], string.Empty),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "(a+)+$";
        vm.TestText = new string('a', 10);
        vm.FlushPendingMatch();

        Assert.Equal("Regex evaluation timed out (ReDoS protection)", vm.StatusText);
        Assert.Equal("ErrorTextBrush", vm.StatusForegroundBrushKey);
    }

    [Fact]
    public async Task SuccessWithNoMatches_ShowsResultsPanelAndZeroCount()
    {
        var vm = CreateViewModel(new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.Success, 0, [], string.Empty),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "z";
        vm.TestText = "abc";
        vm.FlushPendingMatch();

        Assert.True(vm.IsResultsPanelVisible);
        Assert.Equal("0 matches", vm.MatchCountText);
        Assert.Empty(vm.HighlightSegments);
    }

    [Fact]
    public async Task InvalidPattern_AfterSuccess_ClearsDisplayedMatchesAndHighlights()
    {
        var service = new SequencedRegexTesterToolService(
            new RegexTestResult(RegexTestStatus.Success, 1, [new RegexMatchInfo(0, 3, "abc", [])], string.Empty),
            new RegexTestResult(RegexTestStatus.InvalidPattern, 0, [], "bad pattern"));
        var vm = CreateViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "abc";
        vm.TestText = "abc";
        vm.FlushPendingMatch();
        vm.PatternText = "[";
        vm.FlushPendingMatch();

        Assert.Empty(vm.DisplayedMatches);
        Assert.Empty(vm.HighlightSegments);
        Assert.False(vm.IsResultsPanelVisible);
    }

    [Fact]
    public async Task Timeout_AfterSuccess_ClearsDisplayedMatchesAndHighlights()
    {
        var service = new SequencedRegexTesterToolService(
            new RegexTestResult(RegexTestStatus.Success, 1, [new RegexMatchInfo(0, 3, "abc", [])], string.Empty),
            new RegexTestResult(RegexTestStatus.MatchTimeout, 0, [], string.Empty));
        var vm = CreateViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "abc";
        vm.TestText = "abc";
        vm.FlushPendingMatch();
        vm.IsSinglelineChecked = true;
        vm.FlushPendingMatch();

        Assert.Empty(vm.DisplayedMatches);
        Assert.Empty(vm.HighlightSegments);
        Assert.False(vm.IsResultsPanelVisible);
        Assert.Equal("Regex evaluation timed out (ReDoS protection)", vm.StatusText);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsTruncatedNoticeWithoutRerun()
    {
        var matches = Enumerable.Range(0, 501).Select(i => new RegexMatchInfo(i, 1, "a", [])).ToArray();
        var service = new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.Success, matches.Length, matches, string.Empty),
        };
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel(service);
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.PatternText = "a";
        vm.TestText = new string('a', 501);
        vm.FlushPendingMatch();
        var english = vm.TruncatedNoticeText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(1, service.CallCount);
        Assert.NotEqual(english, vm.TruncatedNoticeText);
        Assert.Contains("Affichage", vm.TruncatedNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessWithMatches_PopulatesDisplayItemsAndCopyText()
    {
        var result = new RegexTestResult(
            RegexTestStatus.Success,
            1,
            [new RegexMatchInfo(2, 3, "abc", [new RegexGroupInfo(1, "1", 2, 3, "abc", false)])],
            string.Empty);
        var vm = CreateViewModel(new FakeRegexTesterToolService { Result = result });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "(abc)";
        vm.TestText = "zzabc";
        vm.FlushPendingMatch();

        Assert.Single(vm.DisplayedMatches);
        Assert.Contains("[0] Index 2", vm.DisplayedMatches[0].DisplayText, StringComparison.Ordinal);
        Assert.Contains("Group 1", vm.MatchesCopyText, StringComparison.Ordinal);
        Assert.Single(vm.HighlightSegments, s => s.Kind != RegexHighlightKind.Normal);
    }

    [Fact]
    public async Task NamedGroup_MarksHighlightSegmentAsNamedGroupMatch()
    {
        var result = new RegexTestResult(
            RegexTestStatus.Success,
            1,
            [new RegexMatchInfo(0, 3, "abc", [new RegexGroupInfo(1, "word", 0, 3, "abc", true)])],
            string.Empty);
        var vm = CreateViewModel(new FakeRegexTesterToolService { Result = result });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "(?<word>abc)";
        vm.TestText = "abc";
        vm.FlushPendingMatch();

        Assert.Single(vm.HighlightSegments, segment => segment.Kind == RegexHighlightKind.NamedGroupMatch);
    }

    [Fact]
    public async Task Truncation_UsesFiveHundredDisplayedMatches_AndAddsNoticeToCopyText()
    {
        var matches = Enumerable.Range(0, 501)
            .Select(i => new RegexMatchInfo(i, 1, "a", []))
            .ToArray();
        var vm = CreateViewModel(new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(RegexTestStatus.Success, matches.Length, matches, string.Empty),
        });
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.PatternText = "a";
        vm.TestText = new string('a', 501);
        vm.FlushPendingMatch();

        Assert.Equal(RegexTesterViewModel.MaxDisplayedMatches, vm.DisplayedMatches.Count);
        Assert.True(vm.IsTruncatedNoticeVisible);
        Assert.Contains("Showing first 500 of 501 matches", vm.MatchesCopyText, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefillPattern_DoesNotRunBeforeMarkInitialized()
    {
        var service = new FakeRegexTesterToolService();
        var vm = CreateViewModel(service);
        vm.Initialize(null);

        vm.PrefillPattern("abc");

        Assert.Equal("abc", vm.PatternText);
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public void MarkInitialized_ThenInputChange_SchedulesAndFlushes()
    {
        var service = new FakeRegexTesterToolService();
        var vm = CreateViewModel(service);
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.PatternText = "abc";
        vm.TestText = "abc";

        vm.FlushPendingMatch();

        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsStatusAndDisplayedMatchesWithoutRerun()
    {
        var service = new FakeRegexTesterToolService
        {
            Result = new RegexTestResult(
                RegexTestStatus.Success,
                1,
                [new RegexMatchInfo(0, 3, "abc", [new RegexGroupInfo(1, "1", 0, 3, "abc", false)])],
                string.Empty),
        };
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel(service);
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.PatternText = "(abc)";
        vm.TestText = "abc";
        vm.FlushPendingMatch();
        var englishDisplay = vm.DisplayedMatches[0].DisplayText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(1, service.CallCount);
        Assert.NotEqual(englishDisplay, vm.DisplayedMatches[0].DisplayText);
        Assert.Contains("Groupe 1", vm.DisplayedMatches[0].DisplayText, StringComparison.Ordinal);
        Assert.Equal("Expression valide", vm.StatusText);
    }

    [Fact]
    public void FlagChanges_AffectServiceArguments()
    {
        var service = new FakeRegexTesterToolService();
        var vm = CreateViewModel(service);
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.PatternText = "abc";
        vm.TestText = "ABC";
        vm.IsIgnoreCaseChecked = true;
        vm.IsMultilineChecked = true;
        vm.IsSinglelineChecked = true;

        vm.FlushPendingMatch();

        Assert.True(service.LastIgnoreCase);
        Assert.True(service.LastMultiline);
        Assert.True(service.LastSingleline);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var vm = CreateViewModel();

        vm.Dispose();
        vm.Dispose();

        Assert.True(true);
    }

    private static RegexTesterViewModel CreateViewModel(IRegexTesterToolService? service = null)
        => new(service ?? new FakeRegexTesterToolService());

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private sealed class FakeRegexTesterToolService : IRegexTesterToolService
    {
        public RegexTestResult Result { get; set; } = new(RegexTestStatus.Success, 0, [], string.Empty);
        public int CallCount { get; private set; }
        public bool LastIgnoreCase { get; private set; }
        public bool LastMultiline { get; private set; }
        public bool LastSingleline { get; private set; }

        public RegexTestResult Test(string pattern, string input, bool ignoreCase, bool multiline, bool singleline)
        {
            CallCount++;
            LastIgnoreCase = ignoreCase;
            LastMultiline = multiline;
            LastSingleline = singleline;
            return Result;
        }
    }

    private sealed class SequencedRegexTesterToolService(params RegexTestResult[] results) : IRegexTesterToolService
    {
        private readonly Queue<RegexTestResult> _results = new(results);

        public RegexTestResult Test(string pattern, string input, bool ignoreCase, bool multiline, bool singleline)
        {
            return _results.Count > 0
                ? _results.Dequeue()
                : new RegexTestResult(RegexTestStatus.Success, 0, [], string.Empty);
        }
    }
}
