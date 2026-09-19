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
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that command history is bounded in time, and that the bound is applied.
/// </summary>
/// <remarks>
/// The cleanup routine existed from the day history was written and was never called, so
/// the table grew for the life of the install. That cost little while a row held an
/// un-substituted pattern. It costs more now that a row holds what the operator actually
/// typed, which is why the bound ships with the change that put it there.
/// </remarks>
public sealed class CommandHistoryRetentionTests
{
    [Fact]
    public async Task StartupPrunesWithTheDeclaredRetentionWindow()
    {
        var history = new RecordingHistoryService();

        await TwinShellBootstrapper.PruneCommandHistoryAsync(ProviderFor(history));

        Assert.Equal(
            [AppConstants.CommandHistoryRetentionDays],
            history.CleanupCalls);
    }

    /// <summary>
    /// The window has to be passed, not left to the interface default.
    /// </summary>
    /// <remarks>
    /// Measured: the test above cannot see the difference. <c>CleanupOldEntriesAsync</c>
    /// defaults to ninety days and the declared constant is also ninety, so dropping the
    /// argument records the identical value and the assertion stays green. Two numbers
    /// that agree today are not one decision, and the declared one is the decision. This
    /// reads the argument at the call site, which is the only place the difference exists.
    /// </remarks>
    [Fact]
    public void ThePruneStepPassesTheDeclaredWindowExplicitly()
    {
        var logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadBootstrapperSource()),
            "private static async Task PruneOlderThanRetentionAsync(ICommandHistoryService history)");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(
                logic,
                "await history.CleanupOldEntriesAsync(AppConstants.CommandHistoryRetentionDays);"),
            "The prune step no longer passes the declared retention window, so the constant "
            + "documents a decision the code does not make.");
    }

    /// <summary>
    /// The window has to be a real bound, not an accidental zero or a century.
    /// </summary>
    /// <remarks>
    /// Pinned from both sides, because a one-sided assertion passes on the value that
    /// undoes the change: "greater than zero" is satisfied by a thousand years.
    /// </remarks>
    [Fact]
    public void TheRetentionWindowIsBoundedOnBothSides()
    {
        Assert.InRange(AppConstants.CommandHistoryRetentionDays, 1, 400);
    }

    /// <summary>
    /// A history that cannot be pruned is not a reason to refuse to start.
    /// </summary>
    [Fact]
    public async Task AFailureToPruneDoesNotStopStartup()
    {
        var history = new RecordingHistoryService { ThrowOnCleanup = true };

        await TwinShellBootstrapper.PruneCommandHistoryAsync(ProviderFor(history));

        Assert.Single(history.CleanupCalls);
    }

    /// <summary>
    /// The behavioural tests above prove the prune step does the right thing when it runs.
    /// They cannot prove that startup runs it, and commenting the call out is exactly how
    /// this regresses back to a table that grows forever.
    /// </summary>
    [Fact]
    public void StartupCallsThePruneStep()
    {
        var logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(ReadBootstrapperSource()),
            "public static async Task InitializeAsync(IServiceProvider services)");

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, "await PruneCommandHistoryAsync("),
            "InitializeAsync no longer prunes the command history, so the table grows for "
            + "the life of the install.");
    }

    private static string ReadBootstrapperSource()
    {
        var full = Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Services", "TwinShellBootstrapper.cs");
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }

    private static IServiceProvider ProviderFor(ICommandHistoryService history)
    {
        var services = new ServiceCollection();
        services.AddSingleton(history);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// History service that records the retention windows it was asked for, and can refuse
    /// to do the work so the swallow path is exercised rather than assumed.
    /// </summary>
    private sealed class RecordingHistoryService : ICommandHistoryService
    {
        public List<int> CleanupCalls { get; } = [];

        public bool ThrowOnCleanup { get; init; }

        public Task CleanupOldEntriesAsync(int daysToKeep = 90)
        {
            CleanupCalls.Add(daysToKeep);
            return ThrowOnCleanup
                ? throw new InvalidOperationException("The database is locked.")
                : Task.CompletedTask;
        }

        public Task<string> AddCommandAsync(
            string actionId, string generatedCommand, Dictionary<string, string> parameters,
            Platform platform, string actionTitle, string category)
            => Task.FromResult(string.Empty);

        public Task UpdateWithExecutionResultsAsync(
            string historyId, int exitCode, TimeSpan duration, bool success)
            => Task.CompletedTask;

        public Task<IEnumerable<CommandHistory>> GetRecentAsync(int count = 50)
            => Task.FromResult<IEnumerable<CommandHistory>>([]);

        public Task<IEnumerable<CommandHistory>> SearchAsync(
            string? searchText = null,
            DateTime? fromDate = null,
            DateTime? toDate = null,
            Platform? platform = null,
            string? category = null)
            => Task.FromResult<IEnumerable<CommandHistory>>([]);

        public Task<CommandHistory?> GetByIdAsync(string id)
            => Task.FromResult<CommandHistory?>(null);

        public Task DeleteAsync(string id) => Task.CompletedTask;

        public Task DeleteRangeAsync(IEnumerable<string> ids) => Task.CompletedTask;

        public Task<int> GetCountAsync() => Task.FromResult(0);

        public Task ClearAllAsync() => Task.CompletedTask;
    }
}
