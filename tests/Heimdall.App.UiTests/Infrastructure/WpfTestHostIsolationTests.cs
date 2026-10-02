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

using System.Windows.Threading;
using WpfApplication = System.Windows.Application;

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// The host builds the real application, and WPF queues its startup event from the
/// constructor: the first dispatcher pump used to run the whole product startup inside the
/// test process, against the developer's own data root. One run executed an overdue
/// scheduled task and opened an RDP session to a LAN host.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class WpfTestHostIsolationTests
{
    /// <summary>
    /// The scheduled task engine is owned by the main view model, which only the product
    /// container builds, and that container is built only by the product startup, before its
    /// first await. Once built it takes precedence over the host's own container, so
    /// <c>App.Services</c> being anything but the host's is the earliest proof that the
    /// startup ran, and the engine it leads to; a main window is the later one.
    /// </summary>
    [Fact]
    public async Task Host_DoesNotRunTheProductStartup_SoTheSchedulerCannotStart()
    {
        // Behind every item already queued, the startup event included, which WPF queues at
        // the highest priority.
        await WpfTestHost.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

        await WpfTestHost.Dispatcher.InvokeAsync(() =>
        {
            App app = Assert.IsType<App>(WpfApplication.Current);

            Assert.Same(WpfTestHost.HostServices, app.Services);
            Assert.Empty(app.Windows.OfType<MainWindow>());
        }).Task;
    }
}
