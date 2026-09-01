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

/// <summary>
/// The sentence shown over an empty post-connect list is driven by
/// <see cref="ServerDialogViewModel.HasNoPostConnectSteps"/>. Every test here asserts the change
/// notification and not only the value: the property is computed, so it is right at any instant a
/// test cares to read it, and a missing notification is invisible to a value-only assertion while
/// being exactly the defect the user sees - a message left standing over a populated list.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogPostConnectEmptyStateTests
{
    [Fact]
    public void ASequenceWithNoStepsReportsTheEmptyState()
    {
        var viewModel = new ServerDialogViewModel();

        Assert.True(viewModel.HasNoPostConnectSteps);
    }

    [Fact]
    public void AddingTheFirstStepAnnouncesThatTheListIsNoLongerEmpty()
    {
        var viewModel = new ServerDialogViewModel();
        var announced = TrackAnnouncements(viewModel);

        viewModel.AddPostConnectStepCommand.Execute(null);

        Assert.False(viewModel.HasNoPostConnectSteps);
        Assert.Contains(nameof(ServerDialogViewModel.HasNoPostConnectSteps), announced);
    }

    [Fact]
    public void RemovingTheLastStepAnnouncesThatTheListIsEmptyAgain()
    {
        var viewModel = new ServerDialogViewModel();
        viewModel.AddPostConnectStepCommand.Execute(null);
        var announced = TrackAnnouncements(viewModel);

        viewModel.RemovePostConnectStepCommand.Execute(null);

        Assert.True(viewModel.HasNoPostConnectSteps);
        Assert.Contains(nameof(ServerDialogViewModel.HasNoPostConnectSteps), announced);
    }

    /// <summary>
    /// Opening a saved session is the one path that fills the list without any user gesture, and
    /// it runs with dirty tracking suppressed. A notification raised after that suppression check
    /// leaves the empty-state sentence printed across a list that already has rows in it.
    /// </summary>
    [Fact]
    public void LoadingASavedSequenceAnnouncesThatTheListIsNoLongerEmpty()
    {
        var viewModel = new ServerDialogViewModel();
        var announced = TrackAnnouncements(viewModel);

        viewModel.LoadPostConnectSteps([new PostConnectStep { Input = "sudo -i" }]);

        Assert.False(viewModel.HasNoPostConnectSteps);
        Assert.Contains(nameof(ServerDialogViewModel.HasNoPostConnectSteps), announced);
    }

    /// <summary>
    /// Reopening a session whose steps were all removed goes through Clear, which reports a
    /// single Reset rather than a removal. Both guards in the collection handler drop that call,
    /// so a notification raised after either of them never fires on this transition.
    /// </summary>
    [Fact]
    public void ReloadingWithoutStepsAnnouncesThatTheListIsEmptyAgain()
    {
        var viewModel = new ServerDialogViewModel();
        viewModel.LoadPostConnectSteps([new PostConnectStep { Input = "sudo -i" }]);
        var announced = TrackAnnouncements(viewModel);

        viewModel.LoadPostConnectSteps([]);

        Assert.True(viewModel.HasNoPostConnectSteps);
        Assert.Contains(nameof(ServerDialogViewModel.HasNoPostConnectSteps), announced);
    }

    private static List<string> TrackAnnouncements(ServerDialogViewModel viewModel)
    {
        var announced = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                announced.Add(args.PropertyName);
            }
        };

        return announced;
    }
}
