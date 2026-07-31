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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogResolutionProfileTests
{
    [Fact]
    public void NewProfile_DefaultsToAutoResolutionMode()
    {
        var vm = new ServerDialogViewModel();

        Assert.Equal(RdpResolutionMode.Auto, vm.RdpResolutionMode);
        Assert.True(vm.IsAutoResolutionMode);
    }

    [Fact]
    public void NewRdpProfile_ToDto_UsesAutoResolutionMode()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP"
        };

        var dto = vm.ToDto();

        Assert.Equal(RdpResolutionMode.Auto, dto.RdpResolutionMode);
        Assert.False(dto.RdpMultiMonitor);
    }

    [Theory]
    [InlineData(RdpResolutionMode.Auto, true)]
    [InlineData(RdpResolutionMode.Fixed, false)]
    [InlineData(RdpResolutionMode.FitWindow, false)]
    [InlineData(RdpResolutionMode.SmartSizing, false)]
    [InlineData(RdpResolutionMode.Multimon, false)]
    public void IsAutoResolutionMode_TrueOnlyForAutoMode(RdpResolutionMode mode, bool expected)
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "RDP",
            RdpResolutionMode = mode
        };

        Assert.Equal(expected, vm.IsAutoResolutionMode);
    }

    [Theory]
    [InlineData(RdpResolutionMode.Auto, false)]
    [InlineData(RdpResolutionMode.Fixed, true)]
    [InlineData(RdpResolutionMode.FitWindow, true)]
    [InlineData(RdpResolutionMode.SmartSizing, true)]
    [InlineData(RdpResolutionMode.Multimon, true)]
    public void IsAdvancedResolutionExpanded_FalseOnlyForAuto(RdpResolutionMode mode, bool expected)
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "RDP",
            RdpResolutionMode = mode
        };

        Assert.Equal(expected, vm.IsAdvancedResolutionExpanded);
    }

    [Theory]
    [InlineData(RdpResolutionMode.Auto, false)]
    [InlineData(RdpResolutionMode.Fixed, true)]
    [InlineData(RdpResolutionMode.FitWindow, true)]
    [InlineData(RdpResolutionMode.SmartSizing, true)]
    [InlineData(RdpResolutionMode.Multimon, true)]
    public void CanSwitchToAuto_FalseWhenAlreadyAuto_TrueOtherwise(RdpResolutionMode mode, bool expected)
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "RDP",
            RdpResolutionMode = mode
        };

        Assert.Equal(expected, vm.CanSwitchToAuto);
    }

    [Fact]
    public void SwitchToAutoCommand_SetsModeToAuto()
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Fixed
        };

        vm.SwitchToAutoCommand.Execute(null);

        Assert.Equal(RdpResolutionMode.Auto, vm.RdpResolutionMode);
        Assert.True(vm.IsAutoResolutionMode);
        Assert.False(vm.CanSwitchToAuto);
    }

    [Fact]
    public void IsAutoResolutionMode_FalseWhenNotRdp()
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "SSH",
            RdpResolutionMode = RdpResolutionMode.Auto
        };

        Assert.False(vm.IsAutoResolutionMode);
        Assert.False(vm.IsAdvancedResolutionExpanded);
        Assert.False(vm.CanSwitchToAuto);
    }

    [Fact]
    public void IsMultimonAvailable_TrueWhenMultiScreen_AndRdp()
    {
        var vm = new ServerDialogViewModel(screenCount: 2)
        {
            ConnectionType = "RDP"
        };

        Assert.True(vm.IsMultimonAvailable);
    }

    [Fact]
    public void IsMultimonAvailable_FalseWhenSingleScreen()
    {
        var vm = new ServerDialogViewModel(screenCount: 1)
        {
            ConnectionType = "RDP"
        };

        Assert.False(vm.IsMultimonAvailable);
    }

    [Fact]
    public void IsMultimonAvailable_FalseWhenNotRdp()
    {
        var vm = new ServerDialogViewModel(screenCount: 2)
        {
            ConnectionType = "SSH"
        };

        Assert.False(vm.IsMultimonAvailable);
    }
}
