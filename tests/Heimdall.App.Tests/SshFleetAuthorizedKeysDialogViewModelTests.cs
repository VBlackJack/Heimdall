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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class SshFleetAuthorizedKeysDialogViewModelTests : IDisposable
{
    private readonly string _rootDir = Path.Combine(
        Path.GetTempPath(),
        "heimdall-fleet-ak-vm-" + Guid.NewGuid().ToString("N"));

    public SshFleetAuthorizedKeysDialogViewModelTests()
    {
        Directory.CreateDirectory(_rootDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDir))
            {
                Directory.Delete(_rootDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ScanAndRevoke_PopulatesMatchesUpdatesSelectionAndRevokesViaDialogConfirmation()
    {
        var config = new ConfigManager(_rootDir);
        await config.InitializeAsync();
        await config.SaveServersAsync(
        [
            new ServerProfileDto { Id = "srv-1", DisplayName = "App1", ConnectionType = "SSH", RemoteServer = "app1.internal", SshPort = 22 },
            new ServerProfileDto { Id = "srv-2", DisplayName = "Web2", ConnectionType = "SSH", RemoteServer = "web2.internal", SshPort = 2222 }
        ]);

        var kp = SshKeyPairGenerator.Generate(SshKeyAlgorithm.Ed25519, comment: "compromised");
        var entry = SshAuthorizedKeysService.ParseLines(kp.PublicKeyOpenSsh)[0];
        var dialog = new SessionCoordinatorPreMountTests.FakeDialogService { ConfirmResult = true };

        bool sawAllowLast = false;
        var vm = new SshFleetAuthorizedKeysDialogViewModel(
            services: null,
            config,
            new LocalizationManager(),
            initialQuery: kp.Fingerprint,
            dialogService: dialog,
            scanOverride: (targets, query, _) =>
            {
                IReadOnlyList<SshFleetAuthorizedKeysHostResult> results =
                [
                    new(targets[0].ServerId, targets[0].DisplayName, "app1.internal", 22, [entry], 2, true),
                    new(targets[1].ServerId, targets[1].DisplayName, "web2.internal", 2222, [entry], 1, true)
                ];
                return Task.FromResult(results);
            },
            revokeOverride: (selections, allowLast, _) =>
            {
                sawAllowLast = allowLast;
                IReadOnlyList<SshFleetKeyRevocationResult> results = selections
                    .Select(s => new SshFleetKeyRevocationResult(
                        s.Target.ServerId,
                        s.Target.DisplayName,
                        s.Target.ConnectionParams.Host,
                        s.Target.ConnectionParams.Port,
                        s.Entry,
                        new SshAuthorizedKeysMutationResult(true)))
                    .ToList();
                return Task.FromResult(results);
            });

        await vm.ScanFleetCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Matches.Count);
        Assert.True(vm.CanExport);
        Assert.False(vm.CanRevokeSelected);

        vm.Matches[0].IsSelected = true;
        vm.NotifySelectionChanged();
        Assert.True(vm.CanRevokeSelected);

        await vm.RevokeSelectedCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmCallCount);
        Assert.False(sawAllowLast);
        Assert.Single(vm.Matches);
        Assert.Contains("Matches found: 2", vm.BuildMarkdownReport(), StringComparison.OrdinalIgnoreCase);
    }
}
