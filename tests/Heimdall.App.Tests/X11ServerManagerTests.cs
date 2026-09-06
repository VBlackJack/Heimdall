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

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the VcXsrv command line Heimdall launches. The only X client Heimdall
/// needs is the local one (DISPLAY=localhost:0.0), so the server must keep its
/// host access control: disabling it exposes every forwarded window to any host
/// that can reach TCP 6000 on the workstation.
/// </summary>
public sealed class X11ServerManagerTests
{
    [Fact]
    public void VcXsrvArguments_DoNotDisableAccessControl()
    {
        string[] arguments = X11ServerManager.VcXsrvArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.DoesNotContain("-ac", arguments);
    }

    [Fact]
    public void VcXsrvArguments_KeepMultiWindowAndClipboardDefaults()
    {
        string[] arguments = X11ServerManager.VcXsrvArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(":0", arguments[0]);
        Assert.Contains("-multiwindow", arguments);
        Assert.Contains("-clipboard", arguments);
    }
}
