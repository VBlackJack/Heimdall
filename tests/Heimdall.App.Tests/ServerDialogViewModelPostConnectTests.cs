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

using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogViewModelPostConnectTests
{
    [Fact]
    public void AddPostConnectStep_AddsDefaultRow()
    {
        var vm = new ServerDialogViewModel();

        vm.AddPostConnectStepCommand.Execute(null);

        var step = Assert.Single(vm.PostConnectSteps);
        Assert.True(step.Enabled);
        Assert.Equal(150, step.DelayMs);
        Assert.Equal(PostConnectFailurePolicy.Continue, step.OnFailure);
        Assert.Same(step, vm.SelectedPostConnectStep);
    }

    [Fact]
    public void RemovePostConnectStep_RemovesSelectedRow()
    {
        var vm = new ServerDialogViewModel();
        vm.AddPostConnectStepCommand.Execute(null);
        vm.AddPostConnectStepCommand.Execute(null);
        vm.SelectedPostConnectStep = vm.PostConnectSteps[0];

        vm.RemovePostConnectStepCommand.Execute(null);

        Assert.Single(vm.PostConnectSteps);
        Assert.Same(vm.PostConnectSteps[0], vm.SelectedPostConnectStep);
    }

    [Fact]
    public void MovePostConnectStepUp_UpdatesOrder()
    {
        var vm = new ServerDialogViewModel();
        vm.LoadPostConnectSteps(
        [
            new PostConnectStep { Id = "1", Input = "pwd" },
            new PostConnectStep { Id = "2", Input = "whoami" }
        ]);
        vm.SelectedPostConnectStep = vm.PostConnectSteps[1];

        vm.MovePostConnectStepUpCommand.Execute(null);

        Assert.Equal("whoami", vm.PostConnectSteps[0].Input);
        Assert.Equal("pwd", vm.PostConnectSteps[1].Input);
    }

    [Fact]
    public void ToDto_ExportsStructuredSteps()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Test",
            RemoteServer = "host",
            ConnectionType = "SSH"
        };
        vm.AddPostConnectStepCommand.Execute(null);
        vm.PostConnectSteps[0].Input = "pwd";
        vm.PostConnectSteps[0].DelayMs = 250;
        vm.PostConnectSteps[0].OnFailure = PostConnectFailurePolicy.Stop;

        var dto = vm.ToDto();

        var step = Assert.Single(dto.PostConnectSteps);
        Assert.Equal("pwd", step.Input);
        Assert.Equal(250, step.DelayMs);
        Assert.Equal(PostConnectFailurePolicy.Stop, step.OnFailure);
    }

    [Fact]
    public void FromDto_MigratesLegacyCommandAndExposesLegacyPreview()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Legacy",
            RemoteServer = "host",
            ConnectionType = "SSH",
            PostConnectCommand = "pwd\nwhoami",
            PostConnectDelayMs = 800
        });

        Assert.True(vm.HasLegacyPostConnectCommand);
        Assert.Equal("pwd\nwhoami", vm.LegacyPostConnectCommandText);
        Assert.Equal("800", vm.LegacyPostConnectDelayText);
        Assert.Collection(
            vm.PostConnectSteps,
            step => Assert.Equal("pwd", step.Input),
            step => Assert.Equal("whoami", step.Input));
    }

    [Fact]
    public async Task LinkCommandLibrary_PreservesInputAndStoresLink()
    {
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider();
        var dialogService = new FakeDialogService(new CommandLibraryPickerResult(
            "tail-log",
            "Tail log",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "/var/log/app.log" }));
        var vm = new ServerDialogViewModel
        {
            Localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            DialogService = dialogService,
            ServiceScopeFactory = provider.GetRequiredService<IServiceScopeFactory>()
        };
        vm.AddPostConnectStepCommand.Execute(null);
        vm.PostConnectSteps[0].Input = "pwd";
        vm.RemoteServer = "server.example.com";
        vm.ConnectionType = "SSH";
        vm.EndpointPort = 2222;
        vm.SshUsername = "alice";

        await vm.LinkCommandLibraryCommand.ExecuteAsync(vm.PostConnectSteps[0]);

        var step = vm.PostConnectSteps[0];
        Assert.Equal("pwd", step.Input);
        Assert.Equal("tail-log", step.CommandLibraryId);
        Assert.Equal("Tail log", step.LinkedActionTitle);
        Assert.Equal("/var/log/app.log", step.CommandLibraryParams!["path"]);
        Assert.True(step.IsLinked);
        Assert.NotNull(dialogService.LastPrefillContext);
        Assert.Equal("server.example.com", dialogService.LastPrefillContext!.Host);
        Assert.Equal(2222, dialogService.LastPrefillContext.Port);
        Assert.Equal("alice", dialogService.LastPrefillContext.Username);
    }

    [Fact]
    public async Task LinkCommandLibrary_TelnetPreservesPortWithoutPrefillingHiddenUsername()
    {
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider();
        var dialogService = new FakeDialogService(null);
        var vm = new ServerDialogViewModel
        {
            Localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            DialogService = dialogService,
            ServiceScopeFactory = provider.GetRequiredService<IServiceScopeFactory>(),
            RemoteServer = "telnet.example.com",
            ConnectionType = "TELNET",
            RemotePort = 2323,
            TelnetUsername = "historical-user"
        };
        vm.AddPostConnectStepCommand.Execute(null);

        await vm.LinkCommandLibraryCommand.ExecuteAsync(vm.PostConnectSteps[0]);

        Assert.NotNull(dialogService.LastPrefillContext);
        Assert.Equal("telnet.example.com", dialogService.LastPrefillContext!.Host);
        Assert.Equal(2323, dialogService.LastPrefillContext.Port);
        Assert.Null(dialogService.LastPrefillContext.Username);
    }

    [Fact]
    public void UnlinkCommandLibrary_ClearsLinkAndKeepsDormantInput()
    {
        var vm = new ServerDialogViewModel();
        vm.LoadPostConnectSteps(
        [
            new PostConnectStep
            {
                Id = "1",
                Input = "pwd",
                CommandLibraryId = "tail-log",
                CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "/var/log/app.log" }
            }
        ]);

        vm.UnlinkCommandLibraryCommand.Execute(vm.PostConnectSteps[0]);

        var step = vm.PostConnectSteps[0];
        Assert.Equal("pwd", step.Input);
        Assert.Null(step.CommandLibraryId);
        Assert.Null(step.CommandLibraryParams);
        Assert.False(step.IsLinked);
    }

    [Fact]
    public async Task InitializePostConnectLinksAsync_ResolvesTitlesAndMarksMissingEntriesBroken()
    {
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction("tail-log", "Tail log", "tail -f {path}", CommandLibraryTestHelpers.RequiredParameter("path", "Path")));
        var vm = new ServerDialogViewModel
        {
            ServiceScopeFactory = provider.GetRequiredService<IServiceScopeFactory>()
        };
        vm.LoadPostConnectSteps(
        [
            new PostConnectStep { Id = "1", CommandLibraryId = "tail-log", Input = "pwd" },
            new PostConnectStep { Id = "2", CommandLibraryId = "missing", Input = "hostname" }
        ]);

        await vm.InitializePostConnectLinksAsync();

        Assert.Equal("Tail log", vm.PostConnectSteps[0].LinkedActionTitle);
        Assert.False(vm.PostConnectSteps[0].IsBroken);
        Assert.Null(vm.PostConnectSteps[1].LinkedActionTitle);
        Assert.True(vm.PostConnectSteps[1].IsBroken);
    }

    [Fact]
    public void ToDto_PreservesCommandLibraryLinkFields()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Test",
            RemoteServer = "host",
            ConnectionType = "SSH"
        };
        vm.LoadPostConnectSteps(
        [
            new PostConnectStep
            {
                Id = "1",
                Input = "pwd",
                CommandLibraryId = "tail-log",
                CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "/var/log/app.log" }
            }
        ]);

        var dto = vm.ToDto();
        var step = Assert.Single(dto.PostConnectSteps);

        Assert.Equal("tail-log", step.CommandLibraryId);
        Assert.Equal("/var/log/app.log", step.CommandLibraryParams!["path"]);
        Assert.Equal("pwd", step.Input);
    }

    [Fact]
    public async Task ChangeCommandLibrary_PassesExistingParamsAndUpdatesStep()
    {
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider();
        var dialogService = new FakeDialogService(new CommandLibraryPickerResult(
            "connect-log",
            "Connect log",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = "server.example.com",
                ["user"] = "alice"
            }));
        var vm = new ServerDialogViewModel
        {
            Localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            DialogService = dialogService,
            ServiceScopeFactory = provider.GetRequiredService<IServiceScopeFactory>(),
            RemoteServer = "server.example.com",
            ConnectionType = "SSH",
            EndpointPort = 22,
            SshUsername = "alice"
        };
        vm.LoadPostConnectSteps(
        [
            new PostConnectStep
            {
                Id = "1",
                Input = "pwd",
                CommandLibraryId = "tail-log",
                CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["host"] = "legacy.example.com",
                    ["user"] = "legacy-user"
                }
            }
        ]);

        await vm.ChangeCommandLibraryCommand.ExecuteAsync(vm.PostConnectSteps[0]);

        Assert.Equal("tail-log", dialogService.LastExistingActionId);
        Assert.NotNull(dialogService.LastExistingValues);
        Assert.Equal("legacy.example.com", dialogService.LastExistingValues!["host"]);
        Assert.Equal("connect-log", vm.PostConnectSteps[0].CommandLibraryId);
        Assert.Equal("Connect log", vm.PostConnectSteps[0].LinkedActionTitle);
        Assert.Equal("alice", vm.PostConnectSteps[0].CommandLibraryParams!["user"]);
        Assert.False(vm.PostConnectSteps[0].IsBroken);
    }

    private sealed class FakeDialogService(CommandLibraryPickerResult? pickerResult) : Heimdall.App.Services.IDialogService
    {
        public AutoPrefillContext? LastPrefillContext { get; private set; }
        public string? LastExistingActionId { get; private set; }
        public IReadOnlyDictionary<string, string>? LastExistingValues { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info") => Task.FromResult(false);
        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);
        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);
        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);
        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);
        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);
        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null) => Task.FromResult<ProjectDialogResult?>(null);
        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);
        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);
        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);
        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);
        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);
        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);
        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
        {
            LastPrefillContext = prefillContext;
            LastExistingActionId = existingActionId;
            LastExistingValues = existingValues;
            return Task.FromResult(pickerResult);
        }
        public void ShowError(string title, string message) { }
        public void ShowInfo(string title, string message) { }
        public void ShowWarning(string title, string message) { }
    }
}
