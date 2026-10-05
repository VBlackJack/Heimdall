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

using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Session;

namespace Heimdall.App.Tests;

public sealed class SessionCoordinatorTests
{
    [Fact]
    public void ClearPostConnectStateOnUiThread_UsesInjectedDispatcher()
    {
        var dispatcher = new FakeUiDispatcher();
        var coordinator = SessionCoordinator.CreateForTests(dispatcher);
        var tab = new SessionTabViewModel();
        tab.SetPostConnectState(true, "1/2", "Running");

        coordinator.ClearPostConnectStateOnUiThread(tab);

        Assert.Equal(1, dispatcher.InvokeCalls);
        Assert.False(tab.IsPostConnectRunning);
        Assert.Equal(string.Empty, tab.PostConnectProgressText);
        Assert.Equal(string.Empty, tab.PostConnectTooltip);
    }

    // The run continues on the thread pool once the profiles are read, and the progress state
    // drives a command bound to a button: set from there, it threw and the steps never ran.
    // Proved against the mutant that sets the state in place: the dispatcher sees no call.
    [Fact]
    public void SetPostConnectStateOnUiThread_UsesInjectedDispatcher()
    {
        var dispatcher = new FakeUiDispatcher();
        var coordinator = SessionCoordinator.CreateForTests(dispatcher);
        var tab = new SessionTabViewModel();

        coordinator.SetPostConnectStateOnUiThread(tab, "1/3", "Running step 1", () => { });

        Assert.Equal(1, dispatcher.InvokeCalls);
        Assert.True(tab.IsPostConnectRunning);
        Assert.Equal("1/3", tab.PostConnectProgressText);
        Assert.Equal("Running step 1", tab.PostConnectTooltip);
    }

    // The confirmation of imported commands is a modal dialog, which can only be built on the UI
    // thread. Asked from the pool, it threw and the run was abandoned without a word.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmOnUiThreadAsync_AsksThroughTheDispatcherAndReturnsTheAnswer(bool answer)
    {
        var dispatcher = new FakeUiDispatcher(checkAccess: false);
        var coordinator = SessionCoordinator.CreateForTests(dispatcher);
        bool askedOnUiThread = false;

        bool approved = await coordinator.ConfirmOnUiThreadAsync(() =>
        {
            askedOnUiThread = dispatcher.CheckAccess();
            return Task.FromResult(answer);
        });

        Assert.Equal(answer, approved);
        Assert.Equal(1, dispatcher.InvokeAsyncFuncCalls);
        Assert.True(askedOnUiThread);
    }
}
