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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    // A host a tool discovered is filed through the Add dialog from a transient template that
    // carries the DTO default (unconfirmed). What the user saves from that dialog is trusted.
    [Fact]
    public async Task AddDiscoveredServer_OffersADialogThatSavesTrusted()
    {
        using TestHarness harness = TestHarness.Create();

        await harness.Main.AddDiscoveredServerAsync(new SessionLaunchRequest("found.example.com", 22, "SSH"), null);

        ServerDialogViewModel? dialog = await harness.DialogService.ServerDialogShown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(dialog);
        Assert.False(dialog!.IsEditMode);
        Assert.True(dialog.ExecutionConfirmed);
        Assert.False(dialog.ShowExecutionTrustNotice);
        Assert.True(dialog.ToDto().ExecutionConfirmed);
    }
}
