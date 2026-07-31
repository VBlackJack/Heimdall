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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogViewModelRdpOptionsTests
{
    [Fact]
    public void Default_values_for_external_mode_fields_are_false()
    {
        var dto = new ServerProfileDto();

        Assert.False(dto.RdpAdminMode);
        Assert.False(dto.RdpFullScreen);
    }

    [Fact]
    public void RdpAdminMode_and_RdpFullScreen_round_trip()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpAdminMode = true,
            RdpFullScreen = true
        };

        var dto = vm.ToDto();

        Assert.True(dto.RdpAdminMode);
        Assert.True(dto.RdpFullScreen);

        var vm2 = ServerDialogViewModel.FromDto(dto);

        Assert.True(vm2.RdpAdminMode);
        Assert.True(vm2.RdpFullScreen);
    }

    [Fact]
    public void RdpStrictServerAuthentication_round_trips()
    {
        ServerDialogViewModel viewModel = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpNla = true,
            RdpStrictServerAuthentication = true
        };

        ServerProfileDto dto = viewModel.ToDto();

        Assert.True(dto.RdpStrictServerAuthentication);

        ServerDialogViewModel roundTripped = ServerDialogViewModel.FromDto(dto);

        Assert.True(roundTripped.RdpStrictServerAuthentication);
    }

    [Fact]
    public void RdpDomain_round_trips_and_empty_values_map_to_null()
    {
        ServerDialogViewModel vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpUsername = "admin",
            RdpDomain = "CORP"
        };

        ServerProfileDto dto = vm.ToDto();

        Assert.Equal("CORP", dto.RdpDomain);

        ServerDialogViewModel roundTripped = ServerDialogViewModel.FromDto(dto);

        Assert.Equal("CORP", roundTripped.RdpDomain);

        ServerDialogViewModel whitespace = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpDomain = "   "
        };

        ServerProfileDto whitespaceDto = whitespace.ToDto();

        Assert.Null(whitespaceDto.RdpDomain);

        ServerDialogViewModel empty = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpDomain = string.Empty
        };

        ServerProfileDto emptyDto = empty.ToDto();

        Assert.Null(emptyDto.RdpDomain);
    }

    [Theory]
    [InlineData(0x000)]
    [InlineData(0x001)]
    [InlineData(0x100)]
    [InlineData(0x001 | 0x008 | 0x080)]
    [InlineData(0x001 | 0x002 | 0x004 | 0x008 | 0x020 | 0x080 | 0x100)]
    public void Performance_flags_round_trip_through_bool_properties(int flags)
    {
        var dto = new ServerProfileDto { RdpPerformanceFlags = flags };

        var vm = ServerDialogViewModel.FromDto(dto);

        Assert.Equal((flags & 0x001) != 0, vm.RdpPerfDisableWallpaper);
        Assert.Equal((flags & 0x002) != 0, vm.RdpPerfDisableDrag);
        Assert.Equal((flags & 0x004) != 0, vm.RdpPerfDisableAnimations);
        Assert.Equal((flags & 0x008) != 0, vm.RdpPerfDisableThemes);
        Assert.Equal((flags & 0x020) != 0, vm.RdpPerfDisableCursorShadow);
        Assert.Equal((flags & 0x080) != 0, vm.RdpPerfEnableFontSmoothing);
        Assert.Equal((flags & 0x100) != 0, vm.RdpPerfEnableComposition);

        var roundTripped = vm.ToDto();
        Assert.Equal(flags, roundTripped.RdpPerformanceFlags);
    }

    [Fact]
    public void Mutating_perf_bool_updates_the_DTO_bitmask()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto { RdpPerformanceFlags = 0 });

        vm.RdpPerfDisableThemes = true;

        var dto = vm.ToDto();
        Assert.Equal(0x008, dto.RdpPerformanceFlags);
    }

    [Fact]
    public void Mutating_performance_flags_updates_bool_properties()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto { RdpPerformanceFlags = 0 });

        vm.RdpPerformanceFlags = 0x001 | 0x080;

        Assert.True(vm.RdpPerfDisableWallpaper);
        Assert.True(vm.RdpPerfEnableFontSmoothing);
        Assert.False(vm.RdpPerfDisableThemes);
    }

    [Fact]
    public void Rdp_advanced_default_applies_when_add_dialog_selects_rdp()
    {
        var vm = new ServerDialogViewModel();

        vm.Settings = new AppSettings { RdpDialogAdvancedDefault = true };

        Assert.False(vm.IsAdvancedMode);

        vm.IsProtocolSelected = true;

        Assert.True(vm.IsAdvancedMode);
    }

    [Fact]
    public void Rdp_advanced_default_false_closes_advanced_mode_when_applied()
    {
        var vm = new ServerDialogViewModel { IsAdvancedMode = true };

        vm.Settings = new AppSettings { RdpDialogAdvancedDefault = false };
        vm.IsProtocolSelected = true;

        Assert.False(vm.IsAdvancedMode);
    }

    [Fact]
    public void Rdp_advanced_default_applies_to_existing_rdp_profile()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "RDP"
        });

        vm.Settings = new AppSettings { RdpDialogAdvancedDefault = true };

        Assert.True(vm.IsAdvancedMode);
    }

    [Fact]
    public void Rdp_advanced_default_does_not_apply_to_non_rdp_profile()
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "SSH",
            IsProtocolSelected = true
        };

        vm.Settings = new AppSettings { RdpDialogAdvancedDefault = true };

        Assert.False(vm.IsAdvancedMode);
    }

    [Theory]
    [InlineData("RDP", false, true, false, true)]
    [InlineData("RDP", true, false, false, true)]
    [InlineData("RDP", false, true, true, false)]
    [InlineData("SSH", false, true, false, false)]
    [InlineData("RDP", false, false, false, false)]
    public void Rdp_advanced_default_persistence_policy_is_scoped_to_user_driven_rdp_changes(
        string connectionType,
        bool isEditMode,
        bool isProtocolSelected,
        bool isApplyingDefault,
        bool expected)
    {
        var actual = ServerDialogAdvancedModePolicy.ShouldPersistRdpDefault(
            connectionType,
            isEditMode,
            isProtocolSelected,
            isApplyingDefault);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Rdp_resolution_profile_round_trips_and_snaps_fixed_width()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 1919,
            RdpFixedHeight = 1080,
            RdpInitialSmartSizing = false,
            RdpResizeEnableDelayMs = 3000
        };

        var dto = vm.ToDto();

        Assert.Equal(RdpResolutionMode.Fixed, dto.RdpResolutionMode);
        Assert.Equal(1916, dto.RdpFixedWidth);
        Assert.Equal(1080, dto.RdpFixedHeight);
        Assert.False(dto.RdpInitialSmartSizing);
        Assert.Equal(3000, dto.RdpResizeEnableDelayMs);
        Assert.False(dto.RdpMultiMonitor);

        var roundTripped = ServerDialogViewModel.FromDto(dto);

        Assert.Equal(RdpResolutionMode.Fixed, roundTripped.RdpResolutionMode);
        Assert.Equal(1916, roundTripped.RdpFixedWidth);
        Assert.Equal(1080, roundTripped.RdpFixedHeight);
        Assert.False(roundTripped.RdpInitialSmartSizing);
        Assert.Equal(3000, roundTripped.RdpResizeEnableDelayMs);
    }

    [Fact]
    public void Rdp_multimon_mode_drives_multimon_bool_on_save()
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpMultiMonitor = false
        };

        var dto = vm.ToDto();

        Assert.Equal(RdpResolutionMode.Multimon, dto.RdpResolutionMode);
        Assert.True(dto.RdpMultiMonitor);
    }

    [Fact]
    public void Rdp_multimon_monitor_choices_are_populated_from_enumerator()
    {
        var vm = new ServerDialogViewModel(new FakeMonitorEnumerator(
            [
                new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
                new MonitorInfo(1, 1080, 1920, false, @"\\.\DISPLAY2")
            ]))
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon
        };

        Assert.True(vm.IsMultimonAvailable);
        Assert.True(vm.ShowRdpSelectedMonitors);
        Assert.Equal(2, vm.AvailableMonitors.Count);
        Assert.Equal(0, vm.AvailableMonitors[0].Index);
        Assert.Equal(1080, vm.AvailableMonitors[1].Width);
        Assert.Equal(1920, vm.AvailableMonitors[1].Height);
    }

    [Fact]
    public void Rdp_multimon_monitor_choices_hydrate_from_dto()
    {
        var dto = new ServerProfileDto
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 2, 5]
        };

        var vm = ServerDialogViewModel.FromDto(dto, new FakeMonitorEnumerator(
            [
                new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
                new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2"),
                new MonitorInfo(2, 2560, 1440, false, @"\\.\DISPLAY3")
            ]));

        Assert.True(vm.AvailableMonitors[0].IsSelected);
        Assert.False(vm.AvailableMonitors[1].IsSelected);
        Assert.True(vm.AvailableMonitors[2].IsSelected);
    }

    [Fact]
    public void Rdp_multimon_selected_monitor_choices_round_trip_to_dto()
    {
        var vm = new ServerDialogViewModel(new FakeMonitorEnumerator(
            [
                new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
                new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2"),
                new MonitorInfo(2, 2560, 1440, false, @"\\.\DISPLAY3")
            ]))
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon
        };
        vm.AvailableMonitors[0].IsSelected = true;
        vm.AvailableMonitors[2].IsSelected = true;

        var dto = vm.ToDto();

        Assert.Equal(new[] { 0, 2 }, dto.RdpSelectedMonitorIndices);
    }

    [Fact]
    public void Rdp_multimon_refresh_preserves_valid_selected_monitors()
    {
        var enumerator = new FakeMonitorEnumerator(
            [
                [
                    new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
                    new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2"),
                    new MonitorInfo(2, 2560, 1440, false, @"\\.\DISPLAY3")
                ],
                [
                    new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
                    new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2")
                ]
            ]);
        var vm = new ServerDialogViewModel(enumerator)
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon
        };
        vm.AvailableMonitors[1].IsSelected = true;
        vm.AvailableMonitors[2].IsSelected = true;

        vm.RefreshMonitorsCommand.Execute(null);

        Assert.False(vm.AvailableMonitors[0].IsSelected);
        Assert.True(vm.AvailableMonitors[1].IsSelected);
        Assert.Equal(2, vm.AvailableMonitors.Count);
    }

    [Theory]
    [InlineData(RdpResolutionMode.Auto, false, false, false, false)]
    [InlineData(RdpResolutionMode.Fixed, true, true, true, false)]
    [InlineData(RdpResolutionMode.FitWindow, false, false, true, false)]
    [InlineData(RdpResolutionMode.SmartSizing, false, false, false, false)]
    [InlineData(RdpResolutionMode.Multimon, false, false, false, true)]
    public void Rdp_resolution_profile_visibility_matches_mode(
        RdpResolutionMode mode,
        bool fixedFields,
        bool smartSizing,
        bool resizeDelay,
        bool multimonNote)
    {
        var vm = new ServerDialogViewModel
        {
            ConnectionType = "RDP",
            RdpResolutionMode = mode
        };

        Assert.Equal(fixedFields, vm.ShowRdpFixedResolutionFields);
        Assert.Equal(smartSizing, vm.ShowRdpInitialSmartSizing);
        Assert.Equal(resizeDelay, vm.ShowRdpResizeEnableDelay);
        Assert.Equal(multimonNote, vm.ShowRdpMultimonNote);
    }

    [Fact]
    public void Rdp_resolution_profile_validation_ignores_hidden_fields()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 199,
            RdpFixedHeight = 199,
            RdpResizeEnableDelayMs = 999
        };

        vm.ValidateCommand.Execute(null);

        Assert.Equal(3, vm.OptionsTabErrorCount);
        Assert.Equal(nameof(ServerDialogViewModel.RdpFixedWidth), vm.FirstInvalidField);
        Assert.NotNull(vm.RdpFixedWidthError);
        Assert.NotNull(vm.RdpFixedHeightError);
        Assert.NotNull(vm.RdpResizeEnableDelayMsError);

        vm.RdpResolutionMode = RdpResolutionMode.SmartSizing;
        vm.ValidateCommand.Execute(null);

        Assert.Equal(0, vm.OptionsTabErrorCount);
        Assert.Null(vm.FirstInvalidField);
        Assert.Null(vm.RdpFixedWidthError);
        Assert.Null(vm.RdpFixedHeightError);
        Assert.Null(vm.RdpResizeEnableDelayMsError);
    }

    [Fact]
    public void Rdp_resize_delay_allows_null_or_supported_range()
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.FitWindow,
            RdpResizeEnableDelayMs = null
        };

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.RdpResizeEnableDelayMsError);

        vm.RdpResizeEnableDelayMs = 0;
        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.RdpResizeEnableDelayMsError);

        vm.RdpResizeEnableDelayMs = 60000;
        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.RdpResizeEnableDelayMsError);

        vm.RdpResizeEnableDelayMs = 60001;
        vm.ValidateCommand.Execute(null);

        Assert.NotNull(vm.RdpResizeEnableDelayMsError);
        Assert.Equal(nameof(ServerDialogViewModel.RdpResizeEnableDelayMs), vm.FirstInvalidField);
    }

    [Theory]
    [MemberData(nameof(SessionLoggingOverrideSelectionCases))]
    public void Session_logging_override_selection_maps_to_dto(
        SessionLoggingOverrideSelection selection,
        bool? expectedOverride)
    {
        var vm = new ServerDialogViewModel
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "SSH",
            SessionLoggingOverrideSelection = selection
        };

        var dto = vm.ToDto();

        Assert.Equal(expectedOverride, vm.SessionLoggingOverride);
        Assert.Equal(expectedOverride, dto.SessionLoggingOverride);
    }

    [Theory]
    [MemberData(nameof(SessionLoggingOverrideDtoCases))]
    public void Session_logging_override_loads_from_dto(
        bool? storedOverride,
        SessionLoggingOverrideSelection expectedSelection)
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "SSH",
            SessionLoggingOverride = storedOverride
        });

        Assert.Equal(storedOverride, vm.SessionLoggingOverride);
        Assert.Equal(expectedSelection, vm.SessionLoggingOverrideSelection);
    }

    [Theory]
    [MemberData(nameof(SessionLoggingOverrideDtoCases))]
    public void Session_logging_override_round_trips_through_dto(
        bool? storedOverride,
        SessionLoggingOverrideSelection expectedSelection)
    {
        var original = new ServerProfileDto
        {
            DisplayName = "Server",
            RemoteServer = "server.example.com",
            ConnectionType = "SSH",
            SessionLoggingOverride = storedOverride
        };

        var roundTripped = ServerDialogViewModel.FromDto(original).ToDto();

        Assert.Equal(storedOverride, roundTripped.SessionLoggingOverride);
        Assert.Equal(expectedSelection, ServerDialogViewModel.FromDto(roundTripped).SessionLoggingOverrideSelection);
    }

    public static TheoryData<SessionLoggingOverrideSelection, bool?> SessionLoggingOverrideSelectionCases => new()
    {
        { SessionLoggingOverrideSelection.Inherit, null },
        { SessionLoggingOverrideSelection.On, true },
        { SessionLoggingOverrideSelection.Off, false }
    };

    public static TheoryData<bool?, SessionLoggingOverrideSelection> SessionLoggingOverrideDtoCases => new()
    {
        { null, SessionLoggingOverrideSelection.Inherit },
        { true, SessionLoggingOverrideSelection.On },
        { false, SessionLoggingOverrideSelection.Off }
    };

    private sealed class FakeMonitorEnumerator : IMonitorEnumerator
    {
        private readonly Queue<IReadOnlyList<MonitorInfo>> _snapshots;

        public FakeMonitorEnumerator(IReadOnlyList<MonitorInfo> monitors)
            : this([monitors])
        {
        }

        public FakeMonitorEnumerator(IEnumerable<IReadOnlyList<MonitorInfo>> snapshots)
        {
            _snapshots = new Queue<IReadOnlyList<MonitorInfo>>(snapshots);
        }

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            if (_snapshots.Count > 1)
            {
                return _snapshots.Dequeue();
            }

            return _snapshots.Peek();
        }
    }
}
