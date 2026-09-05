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

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogViewModelRdpOptionsTests
{
    // The domain is forwarded verbatim as a separate MSTSCAX property, so " CORP " reaches the
    // far end with its spaces and is not CORP. Nothing else in the product normalises this value:
    // no Trim, no case fold, no validation on any path between the box and the control. A user
    // who pastes a domain from a wiki page brings the whitespace with it.
    [Theory]
    [InlineData("  CORP  ", "CORP")]
    [InlineData("\tcorp.example.com\r\n", "corp.example.com")]
    [InlineData("CORP", "CORP")]
    public void ToDto_TrimsTheDomain(string typed, string expected)
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "RDP host",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpDomain = typed
        };

        Assert.Equal(expected, vm.ToDto().RdpDomain);
    }

    // Whitespace-only must stay null rather than become an empty string: an empty domain is a
    // real instruction to the resolver, which then derives one from the username instead.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ToDto_WhitespaceOnlyDomain_StaysNull(string? typed)
    {
        ServerDialogViewModel vm = new()
        {
            DisplayName = "RDP host",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpDomain = typed!
        };

        Assert.Null(vm.ToDto().RdpDomain);
    }

    // Same rule as the domain, for the same reason: the gateway is validated and written as an
    // address by the .rdp generator and by the embedded host, and " gw.corp " is not gw.corp to
    // either. It was the one address in the dialog that reached them untrimmed.
    [Theory]
    [InlineData("  gw.corp.example  ", "gw.corp.example")]
    [InlineData("\tgw.corp.example\r\n", "gw.corp.example")]
    [InlineData("gw.corp.example", "gw.corp.example")]
    public void ToDto_TrimsTheGateway(string typed, string expected)
    {
        ServerDialogViewModel vm = RdpProfile();
        vm.RdpGateway = typed;

        Assert.Equal(expected, vm.ToDto().RdpGateway);
    }

    // A malformed gateway used to be saved as typed and refused at connect time, by the
    // attestation sentence the generator and the host raise once the session was already asked
    // for. The same address rule now refuses it at the box, on the tab that shows the box.
    [Fact]
    public void Validate_RefusesAMalformedGateway_AndNamesTheBox()
    {
        ServerDialogViewModel vm = RdpProfile();
        vm.RdpGateway = "gw..corp";

        vm.ValidateCommand.Execute(null);

        Assert.NotNull(vm.RdpGatewayError);
        Assert.Equal(vm.RdpGatewayError, vm.ValidationError);
        Assert.Equal(nameof(ServerDialogViewModel.RdpGateway), vm.FirstInvalidField);
        Assert.Equal(1, vm.NetworkTabErrorCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gw.corp.example")]
    [InlineData(" gw.corp.example ")]
    [InlineData("10.0.0.5")]
    public void Validate_AcceptsAnEmptyOrWellFormedGateway(string typed)
    {
        ServerDialogViewModel vm = RdpProfile();
        vm.RdpGateway = typed;

        vm.ValidateCommand.Execute(null);

        Assert.Null(vm.RdpGatewayError);
        Assert.Equal(0, vm.NetworkTabErrorCount);
    }

    // The error clears as the user types, so the box does not keep refusing a value that has
    // become valid until the next Save.
    [Fact]
    public void Validate_ClearsTheGatewayError_OnceTheValueIsValid()
    {
        ServerDialogViewModel vm = RdpProfile();
        vm.RdpGateway = "gw..corp";
        vm.ValidateCommand.Execute(null);
        Assert.NotNull(vm.RdpGatewayError);

        vm.RdpGateway = "gw.corp.example";

        Assert.Null(vm.RdpGatewayError);
        Assert.Null(vm.ValidationError);
    }

    // The two halves fail independently: a name in the chain with no case in the focus switch
    // leaves the refused Save selecting nothing and focusing nothing. Read as statements of the
    // case's own block, not as a fragment of text.
    [Fact]
    public void TheFocusSwitch_CarriesTheGatewayCase()
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string path = Path.Combine(
            repoRoot, "src", "Heimdall.App", "Views", "Dialogs", "ServerDialog.xaml.cs");
        Assert.True(File.Exists(path), $"Server dialog code-behind not found: {path}");

        string focusMethod = Views.EmbeddedRdp.ViewSource.HandlerBody(
            Views.EmbeddedRdp.ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(path)),
            "private void FocusFirstInvalidField(");

        // The case's statements, wrapped as one body: the label itself is dropped because a
        // statement is only read as one when a brace or a semicolon precedes it.
        const string CaseLabel = "case nameof(ServerDialogViewModel.RdpGateway):";
        string gatewayCase = "{" + Views.EmbeddedRdp.ViewSource.HandlerBody(focusMethod, CaseLabel)[CaseLabel.Length..];

        Assert.True(
            Views.EmbeddedRdp.ViewSource.IsStatementOfTheMethodBody(
                gatewayCase,
                "MainTabControl.SelectedItem = DlgSrv_TabNetwork;"),
            "The gateway case does not select the Network tab, where the gateway box lives.");
        Assert.True(
            Views.EmbeddedRdp.ViewSource.IsStatementOfTheMethodBody(
                gatewayCase,
                "target = DlgSrv_RdpGatewayBox;"),
            "The gateway case does not focus the gateway box.");
    }

    private static ServerDialogViewModel RdpProfile() => new()
    {
        DisplayName = "RDP host",
        RemoteServer = "host.example.com",
        ConnectionType = "RDP"
    };

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

    // The preference is the whole contract. A saved profile whose advanced fields all sit at
    // their conservative defaults used to cancel it, on a judgement the user could neither see
    // nor predict: ticking the box and getting a plain dialog back taught them the box was a lie.
    [Fact]
    public void Rdp_advanced_default_applies_to_a_saved_profile_with_no_advanced_customisation()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto { ConnectionType = "RDP" });
        vm.RdpUseGlobalDefaults = false;
        vm.RdpAntiIdle = false;
        vm.RdpBitmapCaching = true;
        vm.RdpCompression = true;
        vm.RdpAutoReconnect = true;
        vm.RdpAdminMode = false;
        vm.RdpFullScreen = false;
        vm.RdpResolutionMode = RdpResolutionMode.Auto;

        vm.Settings = new AppSettings { RdpDialogAdvancedDefault = true };

        Assert.True(vm.IsAdvancedMode);
    }

    // Every resolution field lives inside the Advanced expander, so a profile saved with a mode
    // other than Auto has to open it even when the preference is off. Collapsed, the resolution
    // section would say nothing at all about the resolution the profile is configured with.
    [Fact]
    public void Rdp_advanced_mode_opens_for_a_non_auto_resolution_profile_without_the_preference()
    {
        var vm = ServerDialogViewModel.FromDto(new ServerProfileDto
        {
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Fixed,
            RdpFixedWidth = 1600,
            RdpFixedHeight = 900
        });

        vm.Settings = new AppSettings { RdpDialogAdvancedDefault = false };

        Assert.True(vm.IsAdvancedMode);
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

    // The defect: the picker is rebuilt from the physically attached screens, so a profile edited
    // on an undocked laptop used to save back only the screens that machine happens to have.
    [Fact]
    public void Rdp_multimon_selection_survives_an_edit_on_a_machine_with_fewer_screens()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Three screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1, 2]
        };

        var vm = ServerDialogViewModel.FromDto(dto, new FakeMonitorEnumerator(
            [
                new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1")
            ]));

        Assert.Equal(new[] { 0, 1, 2 }, vm.ToDto().RdpSelectedMonitorIndices);
    }

    // The counterweight: preserving what the machine cannot show must not preserve what the user
    // deliberately unticked. A union with the seed's whole list would pass the test above and
    // silently ignore every deselection.
    [Fact]
    public void Rdp_multimon_unticking_a_connected_monitor_still_removes_it()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Three screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1, 2]
        };

        var vm = ServerDialogViewModel.FromDto(dto, new FakeMonitorEnumerator(
            [
                new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
                new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2"),
                new MonitorInfo(2, 2560, 1440, false, @"\\.\DISPLAY3")
            ]));

        vm.AvailableMonitors[1].IsSelected = false;

        Assert.Equal(new[] { 0, 2 }, vm.ToDto().RdpSelectedMonitorIndices);
    }

    // Pins where the bookkeeping lives: computing it in FromDto alone would lose the screen that
    // was unplugged while the dialog sat open.
    [Fact]
    public void Rdp_multimon_selection_survives_a_screen_unplugged_while_the_dialog_is_open()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Three screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1, 2]
        };

        // Three snapshots: the constructor takes the first, FromDto's hydration the second, and
        // the refresh below sees the third.
        MonitorInfo[] docked =
        [
            new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
            new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2"),
            new MonitorInfo(2, 2560, 1440, false, @"\\.\DISPLAY3")
        ];
        MonitorInfo[] undocked =
        [
            new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1"),
            new MonitorInfo(1, 1920, 1080, false, @"\\.\DISPLAY2")
        ];

        var vm = ServerDialogViewModel.FromDto(
            dto,
            new FakeMonitorEnumerator([docked, docked, undocked]));

        vm.RefreshMonitorsCommand.Execute(null);

        Assert.Equal(new[] { 0, 1, 2 }, vm.ToDto().RdpSelectedMonitorIndices);
    }

    // A carried index has no checkbox to be read back from, so a rebuild that recomputes the
    // carried set from the picker alone would preserve it once and drop it on the next refresh.
    [Fact]
    public void Rdp_multimon_selection_survives_repeated_refreshes()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Three screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1, 2]
        };

        var vm = ServerDialogViewModel.FromDto(dto, new FakeMonitorEnumerator(
            [
                new MonitorInfo(0, 1920, 1080, true, @"\\.\DISPLAY1")
            ]));

        vm.RefreshMonitorsCommand.Execute(null);
        vm.RefreshMonitorsCommand.Execute(null);

        Assert.Equal(new[] { 0, 1, 2 }, vm.ToDto().RdpSelectedMonitorIndices);
    }

    [Theory]
    [InlineData(1, RdpResolutionMode.Multimon, true)]
    [InlineData(3, RdpResolutionMode.Multimon, false)]
    [InlineData(1, RdpResolutionMode.Auto, false)]
    public void ShowUnavailableSelectedMonitors_OnlyWhenAMultimonProfileHasScreensThisMachineLacks(
        int screenCount,
        RdpResolutionMode mode,
        bool expected)
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Three screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = mode,
            RdpSelectedMonitorIndices = [0, 1, 2]
        };

        var vm = ServerDialogViewModel.FromDto(
            dto,
            new FakeMonitorEnumerator(CreateMonitors(screenCount)));

        Assert.Equal(expected, vm.ShowUnavailableSelectedMonitors);
    }

    // The dialog now opens clean, so an edit that never reaches the view model's own
    // PropertyChanged would be discarded on Escape without a word. Ticking a monitor was one.
    [Fact]
    public void Ticking_a_monitor_arms_the_unsaved_changes_guard()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Two screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0]
        };

        var vm = ServerDialogViewModel.FromDto(
            dto,
            new FakeMonitorEnumerator(CreateMonitors(2)));

        Assert.False(vm.IsDirty);

        vm.AvailableMonitors[1].IsSelected = true;

        Assert.True(vm.IsDirty);
    }

    // The other half of the same rule: a read of the machine is not an edit of the profile. The
    // button re-enumerates the screens and raises the whole derived resolution state, none of it
    // saved by ToDto, so pressing it on a dialog nobody touched used to end on the unsaved-changes
    // prompt.
    [Fact]
    public void Refreshing_the_monitor_list_is_not_an_edit()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Two screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0]
        };

        var vm = ServerDialogViewModel.FromDto(
            dto,
            new FakeMonitorEnumerator(CreateMonitors(2)));

        Assert.False(vm.IsDirty);

        vm.RefreshMonitorsCommand.Execute(null);

        // The refresh really ran: a rebuilt list is what raises the names that used to dirty.
        Assert.Equal(2, vm.AvailableMonitors.Count);
        Assert.True(vm.AvailableMonitors[0].IsSelected);
        Assert.False(vm.IsDirty);
    }

    // A refresh that finds a screen gone must still not claim an edit - and must not lose the
    // monitor it can no longer show, which is what ToDto would then drop.
    [Fact]
    public void Refreshing_after_a_screen_is_unplugged_is_not_an_edit()
    {
        var dto = new ServerProfileDto
        {
            DisplayName = "Two screens",
            RemoteServer = "host.example.com",
            ConnectionType = "RDP",
            RdpResolutionMode = RdpResolutionMode.Multimon,
            RdpSelectedMonitorIndices = [0, 1]
        };

        var vm = ServerDialogViewModel.FromDto(
            dto,
            new FakeMonitorEnumerator([CreateMonitors(2), CreateMonitors(1)]));

        Assert.False(vm.IsDirty);

        vm.RefreshMonitorsCommand.Execute(null);

        Assert.Single(vm.AvailableMonitors);
        Assert.Equal(new[] { 0, 1 }, vm.ToDto().RdpSelectedMonitorIndices);
        Assert.False(vm.IsDirty);
    }

    private static MonitorInfo[] CreateMonitors(int count)
        => [.. Enumerable
            .Range(0, count)
            .Select(index => new MonitorInfo(index, 1920, 1080, index == 0, $@"\\.\DISPLAY{index + 1}"))];

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

    // An RDP profile routes through its RD Gateway exactly as an SSH-family profile routes
    // through a tunnel, and the reachability probe dials neither: it dials the target address
    // from this machine. Off-site, that internal name does not resolve or its 3389 is filtered,
    // so the chip used to state flatly that the host may be off or unreachable - about a host
    // that then connected without trouble through the gateway. The verdict has to name the route
    // it did not take, which is what the SSH half already does.
    [Fact]
    public async Task Rdp_failed_verdict_names_the_RD_Gateway_it_did_not_dial()
    {
        ServerDialogViewModel direct = await RdpProfileAsync();
        ServerDialogViewModel throughGateway = await RdpProfileAsync(RdGatewayHost);

        RdpConnectivityTestResult refused =
            RdpConnectivityTestResult.TcpTimeout("10.0.0.5", TimeSpan.FromSeconds(5));

        direct.ApplyReachabilityResult(refused);
        throughGateway.ApplyReachabilityResult(refused);

        Assert.Contains(RdGatewayHost, throughGateway.TestChipText, StringComparison.Ordinal);
        Assert.StartsWith(direct.TestChipText, throughGateway.TestChipText, StringComparison.Ordinal);
        Assert.NotEqual(direct.TestChipText, throughGateway.TestChipText);
    }

    // The positive control for the assertion above: a gatewayless RDP profile IS tested over the
    // route it connects on, so appending the clause there would be the opposite lie. Without this
    // case, "always append the clause" would pass.
    [Fact]
    public async Task Rdp_without_a_gateway_keeps_the_unqualified_verdict()
    {
        ServerDialogViewModel direct = await RdpProfileAsync();

        direct.ApplyReachabilityResult(
            RdpConnectivityTestResult.TcpTimeout("10.0.0.5", TimeSpan.FromSeconds(5)));

        Assert.DoesNotContain(RdGatewayHost, direct.TestChipText, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ServerDialogReachabilityChipDirectScope",
            direct.TestChipText,
            StringComparison.Ordinal);
    }

    // A success is scoped for the same reason as a failure: what answered is the direct route,
    // which is not the one this profile connects over.
    [Fact]
    public async Task Rdp_successful_verdict_also_names_the_RD_Gateway()
    {
        ServerDialogViewModel direct = await RdpProfileAsync();
        ServerDialogViewModel throughGateway = await RdpProfileAsync(RdGatewayHost);

        RdpConnectivityTestResult answered = RdpConnectivityTestResult.Success(
            "10.0.0.5",
            TimeSpan.FromMilliseconds(4),
            TimeSpan.FromMilliseconds(11));

        direct.ApplyReachabilityResult(answered);
        throughGateway.ApplyReachabilityResult(answered);

        Assert.Contains(RdGatewayHost, throughGateway.TestChipText, StringComparison.Ordinal);
        Assert.StartsWith(direct.TestChipText, throughGateway.TestChipText, StringComparison.Ordinal);
    }

    private const string RdGatewayHost = "rdgw.example.com";

    // The verdict is read from the shipped locale rather than from a stub: a bare
    // LocalizationManager returns key names, under which a routing clause missing its
    // placeholder would still look like a clause.
    private static async Task<ServerDialogViewModel> RdpProfileAsync(string? rdpGateway = null)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(RepoRoot(), "locales"), "en");

        ServerDialogViewModel vm = new()
        {
            DisplayName = "RDP host",
            RemoteServer = "fileserver.corp.local",
            ConnectionType = "RDP",
            RdpGateway = rdpGateway ?? ""
        };

        Assert.False(
            vm.UsesGateway,
            "The fixture must isolate the RD Gateway: an SSH gateway would scope the verdict on "
            + "its own and the assertion would pass for the wrong reason.");

        vm.Localizer = localizer;
        return vm;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

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
