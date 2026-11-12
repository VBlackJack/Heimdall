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
using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.Services.SessionSnapshot;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Shell;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The restore dialog names a session from the whole inventory when the host can resolve it,
/// and from the filtered list otherwise.
/// </summary>
public sealed class SessionRestoreServerResolverTests
{
    private const string HiddenId = "hidden-session";
    private const string HiddenName = "Hidden by a filter";

    [Fact]
    public async Task HostWithAResolver_GivesTheDialogTheResolvedSessions()
    {
        ResolvingHost host = new(restorable: [], resolved: [Item(HiddenId, HiddenName)]);

        SnapshotRestoreDialogViewModel dialog = await ShowDialogAsync(host, HiddenId);

        SnapshotRestoreItemViewModel row = Assert.Single(dialog.Sessions);
        Assert.Equal(HiddenName, row.DisplayName);
        Assert.Equal([HiddenId], host.AskedIds);
    }

    [Fact]
    public async Task HostWithoutAResolver_KeepsTheRestorableServers()
    {
        PlainHost host = new([Item(HiddenId, "From the list")]);

        SnapshotRestoreDialogViewModel dialog = await ShowDialogAsync(host, HiddenId);

        Assert.Equal("From the list", Assert.Single(dialog.Sessions).DisplayName);
    }

    [Fact]
    public async Task ResolverThatFails_FallsBackToTheRestorableServers()
    {
        ResolvingHost host = new(restorable: [Item(HiddenId, "From the list")], resolved: [])
        {
            Failure = new InvalidOperationException("lookup failed"),
        };

        SnapshotRestoreDialogViewModel dialog = await ShowDialogAsync(host, HiddenId);

        Assert.Equal("From the list", Assert.Single(dialog.Sessions).DisplayName);
    }

    [Fact]
    public async Task DuplicateIdsInTheServerList_DoNotThrow()
    {
        PlainHost host = new([Item(HiddenId, "First"), Item(HiddenId.ToUpperInvariant(), "Second")]);

        SnapshotRestoreDialogViewModel dialog = await ShowDialogAsync(host, HiddenId);

        Assert.Equal("First", Assert.Single(dialog.Sessions).DisplayName);
    }

    private static ServerItemViewModel Item(string id, string name)
        => ServerItemViewModel.FromDto(new ServerProfileDto { Id = id, DisplayName = name, ConnectionType = "SSH" });

    private static async Task<SnapshotRestoreDialogViewModel> ShowDialogAsync(ISessionRestoreHost host, string sessionId)
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        IDialogService dialogs = DispatchProxy.Create<IDialogService, CapturingDialogProxy>();
        var capture = (CapturingDialogProxy)dialogs;
        var snapshot = new SessionSnapshotFile
        {
            SavedAtUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            Sessions = [new SessionSnapshotEntry { ServerId = sessionId, ConnectionType = "SSH", Order = 0 }],
        };

        await new SessionRestoreCoordinator(new FixedSnapshotService(snapshot), localizer, dialogs)
            .RestoreAsync(host, CancellationToken.None);

        return capture.Shown ?? throw new InvalidOperationException("the dialog was not shown");
    }

    private sealed class PlainHost(IEnumerable<ServerItemViewModel> restorable) : ISessionRestoreHost
    {
        public IEnumerable<ServerItemViewModel> RestorableServers => restorable;

        public Task<bool> RestoreServerAsync(string originalServerId, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }

    private sealed class ResolvingHost(
        IEnumerable<ServerItemViewModel> restorable,
        IReadOnlyList<ServerItemViewModel> resolved) : ISessionRestoreHost, ISessionRestoreServerResolver
    {
        public List<string> AskedIds { get; } = [];

        public Exception? Failure { get; init; }

        public IEnumerable<ServerItemViewModel> RestorableServers => restorable;

        public Task<bool> RestoreServerAsync(string originalServerId, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<IReadOnlyList<ServerItemViewModel>> ResolveAsync(IReadOnlyCollection<string> ids)
        {
            AskedIds.AddRange(ids);
            return Failure is null ? Task.FromResult(resolved) : Task.FromException<IReadOnlyList<ServerItemViewModel>>(Failure);
        }
    }

    private sealed class FixedSnapshotService(SessionSnapshotFile snapshot) : ISessionSnapshotService
    {
        public string SnapshotPath => "unused";

        public Task SaveAsync(IReadOnlyList<SessionSnapshotEntry> sessions, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SessionSnapshotFile?> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<SessionSnapshotFile?>(snapshot);

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>Answers the snapshot dialog with "no answer" and remembers what it was shown.</summary>
    public class CapturingDialogProxy : DispatchProxy
    {
        public SnapshotRestoreDialogViewModel? Shown { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowSnapshotRestoreDialogAsync))
            {
                Shown = (SnapshotRestoreDialogViewModel)args![0]!;
                return Task.FromResult<SnapshotRestoreDialogResult?>(null);
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
