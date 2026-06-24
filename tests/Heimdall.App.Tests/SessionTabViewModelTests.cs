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

using System.ComponentModel;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Tunnels;
using Heimdall.Core.Configuration;
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.App.Tests;

public sealed class SessionTabViewModelTests
{
    [Fact]
    public void HasFailureDetails_IsFalse_WhenFailureDetailsIsNull()
    {
        var vm = new SessionTabViewModel();

        Assert.Null(vm.FailureDetails);
        Assert.False(vm.HasFailureDetails);
    }

    [Fact]
    public void SettingFailureDetails_RaisesPropertyChanged_AndSetsHasFailureDetails()
    {
        var vm = new SessionTabViewModel();
        List<string> changes = [];
        vm.PropertyChanged += (_, args) => RecordChange(args, changes);

        vm.FailureDetails = new SessionDiagnostic(
            SessionFailureStage.SshAuth,
            "ErrorSshAuthRejected",
            7,
            "Access denied");

        Assert.True(vm.HasFailureDetails);
        Assert.Contains(nameof(SessionTabViewModel.FailureDetails), changes);
        Assert.Contains(nameof(SessionTabViewModel.HasFailureDetails), changes);
    }

    [Fact]
    public void ClearingFailureDetails_ResetsHasFailureDetails()
    {
        var vm = new SessionTabViewModel
        {
            FailureDetails = new SessionDiagnostic(
                SessionFailureStage.SshGateway,
                "ErrorConnectionFailed",
                null,
                "Tunnel failed")
        };

        List<string> changes = [];
        vm.PropertyChanged += (_, args) => RecordChange(args, changes);

        vm.FailureDetails = null;

        Assert.Null(vm.FailureDetails);
        Assert.False(vm.HasFailureDetails);
        Assert.Contains(nameof(SessionTabViewModel.FailureDetails), changes);
        Assert.Contains(nameof(SessionTabViewModel.HasFailureDetails), changes);
    }

    [Fact]
    public void MarkAsAdHoc_SetsFlagAndSnapshot()
    {
        var vm = new SessionTabViewModel();
        var dto = new ServerProfileDto
        {
            Id = "adhoc-rdp-10.0.0.5",
            DisplayName = "RDP to 10.0.0.5",
            RemoteServer = "10.0.0.5",
            ConnectionType = "RDP"
        };

        Assert.False(vm.IsAdHoc);
        Assert.Null(vm.AdHocProfileSnapshot);
        Assert.Throws<ArgumentNullException>(() => vm.MarkAsAdHoc(null!));

        vm.MarkAsAdHoc(dto);

        Assert.True(vm.IsAdHoc);
        Assert.Same(dto, vm.AdHocProfileSnapshot);
    }

    [Fact]
    public void ProfileLookupServerId_UsesPrimaryPaneFallbackRule()
    {
        SessionTabViewModel vm = new SessionTabViewModel
        {
            ServerId = "server-1",
            OriginalServerId = ""
        };

        Assert.Equal("server-1", vm.ProfileLookupServerId);

        vm.OriginalServerId = "profile-1";

        Assert.Equal("profile-1", vm.ProfileLookupServerId);
    }

    [Fact]
    public void TunnelsPanelManualOverride_DefaultsToNullAndRaisesPropertyChanged()
    {
        var vm = new SessionTabViewModel();
        List<string> changes = [];
        vm.PropertyChanged += (_, args) => RecordChange(args, changes);

        Assert.Null(vm.TunnelsPanelManualOverride);

        vm.TunnelsPanelManualOverride = true;

        Assert.True(vm.TunnelsPanelManualOverride);
        Assert.Contains(nameof(SessionTabViewModel.TunnelsPanelManualOverride), changes);
    }

    [Fact]
    public void TunnelBadgeState_DefaultsToHiddenAndRaisesPropertyChanged()
    {
        var vm = new SessionTabViewModel();
        List<string> changes = [];
        vm.PropertyChanged += (_, args) => RecordChange(args, changes);

        Assert.Equal(TunnelBadgeState.Hidden, vm.TunnelBadgeState);

        vm.TunnelBadgeState = TunnelBadgeState.Healthy;

        Assert.Equal(TunnelBadgeState.Healthy, vm.TunnelBadgeState);
        Assert.Contains(nameof(SessionTabViewModel.TunnelBadgeState), changes);
    }

    [Fact]
    public void DisplayTitle_AppendsRdpOverrideSuffix()
    {
        var vm = new SessionTabViewModel
        {
            Title = "Prod RDP"
        };

        Assert.Equal("Prod RDP", vm.DisplayTitle);

        vm.RdpModeOverride = RdpModeOverride.ForceEmbedded;
        vm.RdpModeOverrideSuffix = "(forced embedded)";

        Assert.Equal("Prod RDP (forced embedded)", vm.DisplayTitle);
        Assert.Equal("Prod RDP (forced embedded)", vm.HeaderToolTip);
    }

    [Fact]
    public void CancelPostConnectCommand_IgnoresDisposedCancellationSourceRace()
    {
        SessionTabViewModel vm = new();
        Action cancelAction = static () => throw new ObjectDisposedException("CancellationTokenSource");
        vm.SetPostConnectState(true, "1/2", "Running", cancelAction);

        Exception? exception = Record.Exception(() => vm.CancelPostConnectCommand.Execute(null));

        Assert.True(vm.CancelPostConnectCommand.CanExecute(null));
        Assert.Null(exception);
    }

    [Fact]
    public void DisplayTitle_CustomTitle_OverridesTitleAndSuffix()
    {
        var vm = new SessionTabViewModel
        {
            Title = "Prod RDP",
            RdpModeOverride = RdpModeOverride.ForceEmbedded,
            RdpModeOverrideSuffix = "(forced embedded)"
        };

        Assert.Equal("Prod RDP (forced embedded)", vm.DisplayTitle);

        vm.CustomTitle = "My DB";

        Assert.Equal("My DB", vm.DisplayTitle);
        Assert.Equal("My DB", vm.HeaderToolTip);
    }

    [Fact]
    public void DisplayTitle_BlankCustomTitle_FallsBackToAutoTitle()
    {
        var vm = new SessionTabViewModel { Title = "Auto" };

        vm.CustomTitle = "   ";

        Assert.Equal("Auto", vm.DisplayTitle);
    }

    [Fact]
    public void CustomTitle_SetThenCleared_RestoresAutoTitle()
    {
        var vm = new SessionTabViewModel { Title = "Auto" };

        vm.CustomTitle = "Custom";
        Assert.Equal("Custom", vm.DisplayTitle);

        vm.CustomTitle = null;
        Assert.Equal("Auto", vm.DisplayTitle);
    }

    [Fact]
    public void CustomTitle_Change_RaisesDisplayTitleNotification()
    {
        var vm = new SessionTabViewModel { Title = "Auto" };
        List<string> changes = [];
        vm.PropertyChanged += (_, args) => RecordChange(args, changes);

        vm.CustomTitle = "Custom";

        Assert.Contains(nameof(SessionTabViewModel.CustomTitle), changes);
        Assert.Contains(nameof(SessionTabViewModel.DisplayTitle), changes);
    }

    [Fact]
    public void IsPinned_DefaultsFalse_AndRaisesPropertyChanged()
    {
        var vm = new SessionTabViewModel();
        List<string> changes = [];
        vm.PropertyChanged += (_, args) => RecordChange(args, changes);

        Assert.False(vm.IsPinned);

        vm.IsPinned = true;

        Assert.True(vm.IsPinned);
        Assert.Contains(nameof(SessionTabViewModel.IsPinned), changes);
    }

    private static void RecordChange(PropertyChangedEventArgs args, ICollection<string> changes)
    {
        if (!string.IsNullOrWhiteSpace(args.PropertyName))
        {
            changes.Add(args.PropertyName);
        }
    }
}
