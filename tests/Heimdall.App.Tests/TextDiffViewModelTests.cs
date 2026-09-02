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

public sealed class TextDiffViewModelTests
{
    [Fact]
    public void Ctor_DoesNotRunService()
    {
        var service = new FakeTextDiffToolService();
        _ = new TextDiffViewModel(service);

        Assert.Equal(0, service.DiffCallCount);
    }

    [Fact]
    public void Initialize_SetsLocalizer_DoesNotPrefill()
    {
        var vm = new TextDiffViewModel(new FakeTextDiffToolService());

        vm.Initialize(null);

        Assert.Equal(string.Empty, vm.OriginalText);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void ApplyPrefill_NonEmptyArgument_SetsOriginalText()
    {
        var vm = new TextDiffViewModel(new FakeTextDiffToolService());

        vm.ApplyPrefill("alpha");

        Assert.Equal("alpha", vm.OriginalText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ApplyPrefill_NullOrEmpty_DoesNotTouchOriginalText(string? value)
    {
        var vm = new TextDiffViewModel(new FakeTextDiffToolService());

        vm.ApplyPrefill(value);

        Assert.Equal(string.Empty, vm.OriginalText);
    }

    [Fact]
    public void AutoCompareOff_TextChange_DoesNotRunDiff()
    {
        var service = new FakeTextDiffToolService();
        var vm = CreateViewModel(service);

        vm.OriginalText = "alpha";
        vm.ModifiedText = "beta";

        Assert.Equal(0, service.DiffCallCount);
    }

    [Fact]
    public void AutoCompareOn_TextChange_RunsDiffAfterFlush()
    {
        var service = new FakeTextDiffToolService();
        var vm = CreateViewModel(service);
        vm.AutoCompare = true;
        vm.OriginalText = "alpha";
        vm.ModifiedText = "beta";

        vm.FlushPendingDiff();

        Assert.Equal(1, service.DiffCallCount);
    }

    [Fact]
    public async Task IgnoreWhitespaceToggle_WithInput_RunsDiffImmediately()
    {
        var service = new FakeTextDiffToolService();
        var vm = CreateViewModel(service);
        vm.AutoCompare = true;
        vm.OriginalText = "alpha";
        vm.ModifiedText = "beta";
        vm.FlushPendingDiff();

        vm.IgnoreWhitespace = true;
        await WaitForConditionAsync(() => service.DiffCallCount == 2);

        Assert.Equal(2, service.DiffCallCount);
    }

    [Fact]
    public async Task IgnoreCaseToggle_WithInput_RunsDiffImmediately()
    {
        var service = new FakeTextDiffToolService();
        var vm = CreateViewModel(service);
        vm.AutoCompare = true;
        vm.OriginalText = "alpha";
        vm.ModifiedText = "beta";
        vm.FlushPendingDiff();

        vm.IgnoreCase = true;
        await WaitForConditionAsync(() => service.DiffCallCount == 2);

        Assert.Equal(2, service.DiffCallCount);
    }

    [Fact]
    public void FlagToggle_WithNoInput_DoesNotRunDiff()
    {
        var service = new FakeTextDiffToolService();
        var vm = CreateViewModel(service);

        vm.IgnoreWhitespace = true;
        vm.IgnoreCase = true;

        Assert.Equal(0, service.DiffCallCount);
    }

    [Fact]
    public void TooLargeInput_SetsStatusInputTooLarge_AndClearsResults()
    {
        var vm = CreateViewModel(new FakeTextDiffToolService
        {
            DiffResult = new TextDiffResult(DiffStatus.InputTooLarge, [], 0, 0, 0),
        });
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.OriginalText = "alpha";

        vm.FlushPendingDiff();

        Assert.False(vm.HasResults);
        Assert.Equal(string.Empty, vm.StatsText);
        Assert.Contains("ToolDiffStatusTooLarge", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Swap_TransposesOriginalAndModified()
    {
        var vm = CreateViewModel();
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.OriginalText = "left";
        vm.ModifiedText = "right";

        vm.SwapCommand.Execute(null);

        Assert.Equal("right", vm.OriginalText);
        Assert.Equal("left", vm.ModifiedText);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var vm = CreateViewModel();
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.OriginalText = "left";
        vm.ModifiedText = "right";
        vm.FlushPendingDiff();

        vm.ClearCommand.Execute(null);

        Assert.Equal(string.Empty, vm.OriginalText);
        Assert.Equal(string.Empty, vm.ModifiedText);
        Assert.Empty(vm.DiffLines);
        Assert.Equal(string.Empty, vm.StatusText);
        Assert.Equal(string.Empty, vm.StatsText);
        Assert.Equal(string.Empty, vm.UnifiedDiffText);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public void SuccessfulDiff_PopulatesDiffLinesWithWordDiffOnPairedRows()
    {
        var vm = CreateViewModel(new FakeTextDiffToolService
        {
            DiffResult = new TextDiffResult(
                DiffStatus.Success,
                [new DiffLine(DiffLineKind.Removed, "alpha beta"), new DiffLine(DiffLineKind.Added, "alpha gamma")],
                1,
                1,
                0),
            WordDiffResult = new WordDiffResult(
                [new WordSegment("alpha ", false), new WordSegment("beta", true)],
                [new WordSegment("alpha ", false), new WordSegment("gamma", true)]),
        });
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.OriginalText = "alpha beta";
        vm.ModifiedText = "alpha gamma";

        vm.FlushPendingDiff();

        Assert.Equal(2, vm.DiffLines.Count);
        Assert.True(vm.DiffLines[0].Segments.Last().IsChanged);
        Assert.True(vm.DiffLines[1].Segments.Last().IsChanged);
    }

    [Fact]
    public async Task StatsText_MatchesDiffStatsKey_FormattedWithCounts()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new TextDiffViewModel(new TextDiffToolService());
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.OriginalText = "a\nb";
        vm.ModifiedText = "a\nc";

        vm.FlushPendingDiff();

        Assert.Equal("+1 additions, -1 deletions, 1 unchanged", vm.StatsText);
    }

    [Fact]
    public async Task StatusText_OnDone_MatchesDiffStatusDoneKey()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new TextDiffViewModel(new TextDiffToolService());
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.OriginalText = "a\nb";
        vm.ModifiedText = "a\nc";

        vm.FlushPendingDiff();

        Assert.Equal("Diff complete: 3 lines", vm.StatusText);
    }

    [Fact]
    public async Task OnLocaleChanged_RebuildsUnifiedDiffText_WithoutReRunningDiff()
    {
        var service = new FakeTextDiffToolService();
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel(service);
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.OriginalText = "a";
        vm.ModifiedText = "b";
        vm.FlushPendingDiff();
        var english = vm.UnifiedDiffText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(1, service.DiffCallCount);
        Assert.NotEqual(english, vm.UnifiedDiffText);
        Assert.StartsWith("--- original", vm.UnifiedDiffText, StringComparison.Ordinal);
        Assert.Contains("+++ modifié", vm.UnifiedDiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnLocaleChanged_RebuildsStatsText_WithoutReRunningDiff()
    {
        var service = new FakeTextDiffToolService();
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel(service);
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.OriginalText = "a";
        vm.ModifiedText = "b";
        vm.FlushPendingDiff();

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(1, service.DiffCallCount);
        Assert.Equal("+1 ajouts, -1 suppressions, 0 inchangées", vm.StatsText);
    }

    [Fact]
    public async Task OnLocaleChanged_RebuildsStatusText_WithoutReRunningDiff()
    {
        var service = new FakeTextDiffToolService();
        var localizer = await CreateLocalizerAsync("en");
        var vm = CreateViewModel(service);
        vm.Initialize(localizer);
        vm.MarkInitialized();
        vm.OriginalText = "a";
        vm.ModifiedText = "b";
        vm.FlushPendingDiff();

        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(1, service.DiffCallCount);
        Assert.Equal("Diff terminé : 2 lignes", vm.StatusText);
    }

    [Fact]
    public void WordDiff_OnlyAppliedToConsecutiveRemovedThenAddedPair()
    {
        var service = new FakeTextDiffToolService
        {
            DiffResult = new TextDiffResult(
                DiffStatus.Success,
                [
                    new DiffLine(DiffLineKind.Removed, "one"),
                    new DiffLine(DiffLineKind.Removed, "two"),
                    new DiffLine(DiffLineKind.Added, "three"),
                ],
                1,
                2,
                0),
        };
        var vm = CreateViewModel(service);
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.OriginalText = "one\ntwo";
        vm.ModifiedText = "three";

        vm.FlushPendingDiff();

        Assert.Equal(1, service.WordDiffCallCount);
        Assert.Equal("one", vm.DiffLines[0].Segments[0].Text);
        Assert.False(vm.DiffLines[0].Segments[0].IsChanged);
    }

    [Fact]
    public void UnifiedDiffText_BeginsWithTwoLocalizedHeaderLines()
    {
        var vm = CreateViewModel();
        vm.Initialize(null);
        vm.MarkInitialized();
        vm.OriginalText = "a";
        vm.ModifiedText = "b";

        vm.FlushPendingDiff();

        Assert.StartsWith("ToolDiffOriginalHeader" + Environment.NewLine + "ToolDiffModifiedHeader" + Environment.NewLine, vm.UnifiedDiffText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsBusy_PreventsReEntry()
    {
        var service = new BlockingTextDiffToolService();
        var vm = CreateViewModel(service);
        vm.Initialize(await CreateLocalizerAsync("en"));
        vm.MarkInitialized();
        vm.OriginalText = "a";
        vm.ModifiedText = "b";

        var firstExecution = vm.DiffCommand.ExecuteAsync(null);
        await service.Started.Task;
        Assert.False(vm.DiffCommand.CanExecute(null));

        vm.FlushPendingDiff();
        service.Release.SetResult();
        await firstExecution;

        Assert.Equal(1, service.DiffCallCount);
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var vm = CreateViewModel();

        vm.Dispose();
        vm.Dispose();

        Assert.True(true);
    }

    private static TextDiffViewModel CreateViewModel(ITextDiffToolService? service = null)
    {
        var vm = new TextDiffViewModel(service ?? new FakeTextDiffToolService());
        vm.Initialize(null);
        vm.MarkInitialized();
        return vm;
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 50; i++)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.True(predicate());
    }

    private sealed class FakeTextDiffToolService : ITextDiffToolService
    {
        public TextDiffResult DiffResult { get; set; } = new(
            DiffStatus.Success,
            [new DiffLine(DiffLineKind.Removed, "a"), new DiffLine(DiffLineKind.Added, "b")],
            1,
            1,
            0);

        public WordDiffResult WordDiffResult { get; set; } = new(
            [new WordSegment("a", true)],
            [new WordSegment("b", true)]);

        public int DiffCallCount { get; private set; }
        public int WordDiffCallCount { get; private set; }

        public TextDiffResult Diff(string original, string modified, DiffOptions options)
        {
            DiffCallCount++;
            return DiffResult;
        }

        public WordDiffResult WordDiff(string oldLine, string newLine)
        {
            WordDiffCallCount++;
            return WordDiffResult;
        }
    }

    private sealed class BlockingTextDiffToolService : ITextDiffToolService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DiffCallCount { get; private set; }

        public TextDiffResult Diff(string original, string modified, DiffOptions options)
        {
            DiffCallCount++;
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return new TextDiffResult(DiffStatus.Success, [new DiffLine(DiffLineKind.Added, "b")], 1, 0, 0);
        }

        public WordDiffResult WordDiff(string oldLine, string newLine)
            => new([], []);
    }
}
