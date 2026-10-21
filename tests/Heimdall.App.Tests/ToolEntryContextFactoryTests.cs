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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// Every path that opens a tool entry carries the operating system its profile declares: the
/// tree and its context menu share one builder, the connect pipeline has the other.
/// </summary>
/// <remarks>
/// The builders are asserted rather than the three call sites, which live in a window, a context
/// menu and the connect pipeline; each call site is one line that hands its result straight to
/// the tool opener.
/// </remarks>
public sealed class ToolEntryContextFactoryTests
{
    [Fact]
    public void TheTreeEntryContext_CarriesTheDeclaredOperatingSystem()
    {
        var tool = ServerItemViewModel.FromDto(Profile());

        var context = ToolEntryContextFactory.ForTreeEntry(tool);

        Assert.Equal(RemoteOs.Windows, context.RemoteOs);
        Assert.Equal("srv-01", context.TargetHost);
        Assert.Equal("srv-01", context.Argument);
    }

    [Fact]
    public void TheConnectedEntryContext_CarriesTheDeclaredOperatingSystem()
    {
        var context = ToolEntryContextFactory.ForConnectedEntry(Profile(), "Project");

        Assert.Equal(RemoteOs.Windows, context.RemoteOs);
        Assert.Equal("srv-01", context.TargetHost);
        Assert.Equal("Project", context.ProjectName);
    }

    // An undeclared profile leaves the context on Auto, so the protocol guess still applies.
    [Fact]
    public void AnUndeclaredProfile_LeavesTheContextOnAuto()
    {
        var profile = Profile();
        profile.RemoteOs = RemoteOs.Auto;

        Assert.Equal(RemoteOs.Auto, ToolEntryContextFactory.ForConnectedEntry(profile, null).RemoteOs);
        Assert.Equal(RemoteOs.Auto, ToolEntryContextFactory.ForTreeEntry(ServerItemViewModel.FromDto(profile)).RemoteOs);
    }

    private static ServerProfileDto Profile() => new()
    {
        Id = "tool-1",
        DisplayName = "Library on srv-01",
        ConnectionType = "TOOL:CMDLIB",
        RemoteServer = "srv-01",
        RemoteOs = RemoteOs.Windows
    };
}
