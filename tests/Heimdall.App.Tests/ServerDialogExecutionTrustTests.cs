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
using Heimdall.App.Services.Handlers;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The session dialog must not turn what an imported profile runs into a trusted payload: the
/// flag survives a rename and a save, and only the notice's Allow button changes it.
/// </summary>
public sealed class ServerDialogExecutionTrustTests
{
    // ---- The flag survives the dialog --------------------------------------------------------

    [Fact]
    public void Dialog_ImportedLocal_RenameAndSave_StaysUntrusted()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Profile("LOCAL", dto => dto.LocalShellExecutable = "evil.exe"));

        viewModel.DisplayName = "Renamed";
        ServerProfileDto saved = viewModel.ToDto();

        Assert.False(saved.ExecutionConfirmed);
        Assert.Equal("evil.exe", saved.LocalShellExecutable);
    }

    [Fact]
    public void Dialog_ImportedSshStep_RenameAndSave_StaysUntrusted()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Profile("SSH", dto => dto.PostConnectSteps = [Step("rm -rf /")]));

        viewModel.DisplayName = "Renamed";
        ServerProfileDto saved = viewModel.ToDto();

        Assert.False(saved.ExecutionConfirmed);
        Assert.Equal("rm -rf /", Assert.Single(saved.PostConnectSteps).Input);
    }

    [Fact]
    public void Dialog_TrustedProfile_RoundTrip_StaysTrusted()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(
            Profile("LOCAL", dto =>
            {
                dto.LocalShellExecutable = "pwsh.exe";
                dto.ExecutionConfirmed = true;
            }));

        viewModel.DisplayName = "Renamed";

        Assert.True(viewModel.ToDto().ExecutionConfirmed);
        Assert.False(viewModel.ShowExecutionTrustNotice);
    }

    [Fact]
    public void Dialog_NewProfile_IsTrusted_AndShowsNoNotice()
    {
        ServerDialogViewModel viewModel = new()
        {
            DisplayName = "New",
            RemoteServer = "localhost",
            ConnectionType = "LOCAL",
            LocalShellExecutable = "pwsh.exe",
        };

        Assert.True(viewModel.ToDto().ExecutionConfirmed);
        Assert.False(viewModel.ShowExecutionTrustNotice);
    }

    [Fact]
    public void Dialog_BareImportedLocal_RenameAndSave_AddsNoPayload()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Profile("LOCAL", _ => { }));

        viewModel.DisplayName = "Renamed";
        ServerProfileDto saved = viewModel.ToDto();

        Assert.Null(saved.LocalShellExecutable);
        Assert.False(saved.ExecutionConfirmed);
        Assert.False(ProfileExecutionTrust.CarriesLocalExecutionPayload(saved));
        Assert.False(viewModel.ShowExecutionTrustNotice);
    }

    [Fact]
    public void Dialog_LocalThatNamedTheDefaultShell_KeepsIt_AndAnEditedExecutableIsSavedAsTyped()
    {
        ServerDialogViewModel named = ServerDialogViewModel.FromDto(Profile("LOCAL", dto => dto.LocalShellExecutable = "PowerShell.exe"));
        Assert.Equal("PowerShell.exe", named.ToDto().LocalShellExecutable);

        ServerDialogViewModel bare = ServerDialogViewModel.FromDto(Profile("LOCAL", _ => { }));
        bare.LocalShellExecutable = "pwsh.exe";
        Assert.Equal("pwsh.exe", bare.ToDto().LocalShellExecutable);

        bare.LocalShellExecutable = "   ";
        Assert.Null(bare.ToDto().LocalShellExecutable);
    }

    // ---- When the notice shows ---------------------------------------------------------------

    [Theory]
    [InlineData("local-executable")]
    [InlineData("local-arguments")]
    [InlineData("local-working-directory")]
    [InlineData("local-elevated")]
    [InlineData("web-external")]
    [InlineData("citrix-ica")]
    [InlineData("ssh-step")]
    [InlineData("rdp-step")]
    [InlineData("linked-step")]
    public void Notice_Shown_WhenPayload(string kind)
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile(kind));

        Assert.True(viewModel.ShowExecutionTrustNotice);
    }

    [Theory]
    [InlineData("plain-rdp")]
    [InlineData("plain-local")]
    [InlineData("web-default-browser")]
    [InlineData("web-program-not-external")]
    [InlineData("disabled-step")]
    [InlineData("blank-step")]
    public void Notice_Hidden_WhenNoPayload(string kind)
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile(kind));

        Assert.False(viewModel.ShowExecutionTrustNotice);
    }

    [Fact]
    public void Notice_Hidden_AndNotComputed_WhenConfirmed()
    {
        ServerProfileDto dto = PayloadProfile("local-executable");
        dto.ExecutionConfirmed = true;
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        Assert.False(viewModel.ShowExecutionTrustNotice);
        Assert.Equal(string.Empty, viewModel.ExecutionTrustNoticeText);
    }

    [Fact]
    public void Notice_AppearsAndDisappears_WithThePayloadTheDialogHolds()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile("plain-local"));
        Assert.False(viewModel.ShowExecutionTrustNotice);

        viewModel.LocalShellExecutable = "evil.exe";
        Assert.True(viewModel.ShowExecutionTrustNotice);

        viewModel.LocalShellExecutable = "powershell.exe";
        Assert.False(viewModel.ShowExecutionTrustNotice);
    }

    [Fact]
    public void Notice_LegacyPostConnectCommand_IsCountedAsTheNextLoadWillSeeIt()
    {
        ServerProfileDto dto = Profile("SSH", d => d.PostConnectCommand = "whoami");
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        Assert.True(viewModel.ShowExecutionTrustNotice);
        Assert.Contains("whoami", viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);
    }

    // ---- Allow -------------------------------------------------------------------------------

    [Fact]
    public void Allow_HidesNotice_MarksDirty_SavesTrusted()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile("local-executable"));
        Assert.False(viewModel.IsDirty);
        Assert.True(viewModel.ShowExecutionTrustNotice);

        viewModel.AllowExecutionTrustCommand.Execute(null);

        Assert.True(viewModel.ExecutionConfirmed);
        Assert.False(viewModel.ShowExecutionTrustNotice);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.ToDto().ExecutionConfirmed);
    }

    [Fact]
    public void Allow_Disabled_WhenWebCommandTooLong()
    {
        ServerProfileDto dto = PayloadProfile("web-external");
        dto.WebExternalProgram = new string('a', WebExternalLaunch.MaxDescribedProgramLength + 1);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        Assert.False(viewModel.AllowExecutionTrustCommand.CanExecute(null));
        viewModel.AllowExecutionTrustCommand.Execute(null);
        Assert.False(viewModel.ExecutionConfirmed);
        Assert.Contains(
            viewModel.Localizer![WebExternalLaunch.CommandTooLongErrorKey],
            viewModel.ExecutionTrustNoticeText,
            StringComparison.Ordinal);

        viewModel.WebExternalProgram = "legacy.exe";
        Assert.True(viewModel.AllowExecutionTrustCommand.CanExecute(null));
    }

    // ---- What the notice says ----------------------------------------------------------------

    [Theory]
    [InlineData("local-executable", "evil.exe")]
    [InlineData("local-arguments", "-EncodedCommand AAAA")]
    [InlineData("local-working-directory", "C:\\evil\\dir")]
    [InlineData("local-elevated", "elevated")]
    [InlineData("web-external", "legacy.exe")]
    [InlineData("citrix-ica", "C:\\evil\\launch.ica")]
    [InlineData("ssh-step", "rm -rf /")]
    [InlineData("rdp-step", "calc.exe")]
    public void NoticeText_ListsEveryGatedValue(string kind, string expected)
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile(kind));
        viewModel.Localizer = LoadLocalizer();

        string text = viewModel.ExecutionTrustNoticeText;

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_ListsOneRowPerEnabledStep_AndSkipsTheOthers()
    {
        ServerProfileDto dto = Profile("SSH", d => d.PostConnectSteps =
        [
            Step("first-command"),
            Step("disabled-command", enabled: false),
            Step(string.Empty),
            Step("second-command"),
        ]);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string[] rows = viewModel.ExecutionTrustNoticeText.Split('\n').Skip(1).ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Contains("first-command", rows[0], StringComparison.Ordinal);
        Assert.Contains("second-command", rows[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_LinkedStep_NamesTheEntryTitleAndTheParametersTheProfileSupplies()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile("linked-step"));
        viewModel.Localizer = LoadLocalizer();

        // Without a resolved title the notice falls back to the entry id.
        Assert.Contains("lib-1", viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);

        viewModel.PostConnectSteps[0].LinkedActionTitle = "Restart service";
        string text = viewModel.ExecutionTrustNoticeText;

        Assert.Contains("\"Restart service\"", text, StringComparison.Ordinal);
        Assert.Contains("unit = \"nginx\"", text, StringComparison.Ordinal);
        Assert.Contains("mode = \"fast\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("lib-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_LinkedStepWithoutParameters_StillReadsAsASentence()
    {
        ServerProfileDto dto = Profile("SSH", d => d.PostConnectSteps =
        [
            new PostConnectStep { Id = "s1", CommandLibraryId = "lib-9" },
        ]);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        Assert.True(viewModel.ShowExecutionTrustNotice);
        Assert.EndsWith(
            viewModel.Localizer!.Format("ServerDialogExecutionTrustLibraryCommandNoParameters", "lib-9"),
            viewModel.ExecutionTrustNoticeText,
            StringComparison.Ordinal);
    }

    // The runner types the resolved entry and ignores the Input of a linked step, so the notice
    // lists the entry only: showing the literal text would describe something that is never typed.
    [Fact]
    public void NoticeText_LinkedStepWithInput_ShowsOnlyTheLibraryRow()
    {
        ServerProfileDto dto = Profile("SSH", d => d.PostConnectSteps =
        [
            new PostConnectStep { Id = "s1", CommandLibraryId = "lib-1", Input = "echo literal-input" },
        ]);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string row = Assert.Single(viewModel.ExecutionTrustNoticeText.Split('\n').Skip(1));

        Assert.Contains("lib-1", row, StringComparison.Ordinal);
        Assert.DoesNotContain("literal-input", row, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_LinkedStepParameters_AreEscaped()
    {
        ServerProfileDto dto = Profile("SSH", d => d.PostConnectSteps =
        [
            new PostConnectStep
            {
                Id = "s1",
                CommandLibraryId = "lib-1",
                CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["na\nme\u202e"] = "va\nl\u202eue",
                },
            },
        ]);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string text = viewModel.ExecutionTrustNoticeText;

        Assert.DoesNotContain('\u202e', text);
        Assert.Single(text.Split('\n').Skip(1));
        Assert.Contains("na\\u{000a}me\\u{202e} = \"va\\u{000a}l\\u{202e}ue\"", text, StringComparison.Ordinal);
    }

    // A backslash before the quote must not let a value fake its own closing quote.
    [Fact]
    public void NoticeText_ABackslashBeforeAQuote_CannotCloseTheQuotes()
    {
        ServerProfileDto dto = Profile("SSH", d => d.PostConnectSteps = [Step("ls\\\" ; then curl x|sh")]);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string text = viewModel.ExecutionTrustNoticeText;

        Assert.Contains(@"""ls\\\"" ; then curl x|sh""", text, StringComparison.Ordinal);
    }

    // The flag the importers of old profiles set is an elevation request like the mode itself.
    [Fact]
    public void Notice_LegacyElevatedFlagOnly_IsShownAndListsElevation()
    {
        ServerProfileDto dto = Profile("LOCAL", d =>
        {
            d.LocalShellElevated = true;
            d.ElevationMode = ElevationMode.None;
        });
        Assert.Equal(ElevationMode.Auto, dto.EffectiveElevationMode);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        Assert.True(viewModel.ShowExecutionTrustNotice);
        Assert.Contains(
            viewModel.Localizer!["ConfirmExecutionDetailElevated"],
            viewModel.ExecutionTrustNoticeText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AllowCredentialLookup_DoesNotConfirmExecution()
    {
        ServerProfileDto dto = Profile("SSH", d =>
        {
            d.PostConnectSteps = [Step("rm -rf /")];
            d.CredentialLookupConfirmed = false;
        });
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);

        viewModel.AllowCredentialLookupCommand.Execute(null);
        ServerProfileDto saved = viewModel.ToDto();

        Assert.True(saved.CredentialLookupConfirmed);
        Assert.False(saved.ExecutionConfirmed);
        Assert.True(viewModel.ShowExecutionTrustNotice);
    }

    [Fact]
    public void NoticeText_RefreshesOnStepEditAndAdd()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Profile("RDP", d => d.PostConnectSteps = [Step("first")]));
        viewModel.Localizer = LoadLocalizer();
        List<string?> raised = [];
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.PostConnectSteps[0].Input = "edited";
        Assert.Contains(nameof(ServerDialogViewModel.ExecutionTrustNoticeText), raised);
        Assert.Contains("edited", viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);

        raised.Clear();
        viewModel.PostConnectSteps[0].Enabled = false;
        Assert.Contains(nameof(ServerDialogViewModel.ShowExecutionTrustNotice), raised);
        Assert.False(viewModel.ShowExecutionTrustNotice);

        raised.Clear();
        viewModel.AddPostConnectStepCommand.Execute(null);
        viewModel.PostConnectSteps[1].Input = "added";
        Assert.Contains(nameof(ServerDialogViewModel.ExecutionTrustNoticeText), raised);
        Assert.True(viewModel.ShowExecutionTrustNotice);
        Assert.Contains("added", viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_RaisedOncePerFieldChange_WithoutCountingAsAnEdit()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile("local-executable"));
        viewModel.Localizer = LoadLocalizer();
        viewModel.IsDirty = false;
        List<string?> raised = [];
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.LocalShellArguments = "-NoProfile";

        Assert.Equal(1, raised.Count(name => name == nameof(ServerDialogViewModel.ExecutionTrustNoticeText)));
        Assert.Equal(1, raised.Count(name => name == nameof(ServerDialogViewModel.ShowExecutionTrustNotice)));
        Assert.Contains("-NoProfile", viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_RefreshingTheNotice_DoesNotMarkTheDialogDirty()
    {
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(PayloadProfile("ssh-step"));
        viewModel.Localizer = LoadLocalizer();

        _ = viewModel.ExecutionTrustNoticeText;
        _ = viewModel.ShowExecutionTrustNotice;

        Assert.False(viewModel.IsDirty);
    }

    // ---- Untrusted values --------------------------------------------------------------------

    [Fact]
    public void NoticeText_EscapesBreaksAndBidi()
    {
        ServerProfileDto dto = Profile("SSH", d =>
        {
            d.PostConnectSteps = [Step("ls\nrm \u202e-rf\u200b /\u2028x \"q\"")];
        });
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string text = viewModel.ExecutionTrustNoticeText;
        string row = Assert.Single(text.Split('\n').Skip(1));

        Assert.DoesNotContain('\u202e', text);
        Assert.DoesNotContain('\u200b', text);
        Assert.DoesNotContain('\u2028', text);
        Assert.Contains("ls\\u{000a}rm \\u{202e}-rf\\u{200b} /\\u{2028}x \\\"q\\\"", row, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_ALongCommand_IsNotTruncated()
    {
        string longCommand = new('x', 5000);
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(Profile("SSH", d => d.PostConnectSteps = [Step(longCommand)]));
        viewModel.Localizer = LoadLocalizer();

        Assert.Contains(longCommand, viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);
        Assert.DoesNotContain("...", viewModel.ExecutionTrustNoticeText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_ALocalCommandWithBidiAndZeroWidth_IsMadeVisible()
    {
        ServerProfileDto dto = Profile("LOCAL", d =>
        {
            d.LocalShellExecutable = "ev\u200bil.exe";
            d.LocalShellArguments = "a\u202eb\u0007";
            d.LocalShellWorkingDirectory = "C:\\x\u2029y";
        });
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string text = viewModel.ExecutionTrustNoticeText;

        Assert.DoesNotContain('\u200b', text);
        Assert.DoesNotContain('\u202e', text);
        Assert.DoesNotContain('\u2029', text);
        Assert.DoesNotContain('\u0007', text);
        Assert.Contains("ev\\u{200b}il.exe", text, StringComparison.Ordinal);
        Assert.Contains("a\\u{202e}b\\u{0007}", text, StringComparison.Ordinal);
        Assert.Contains("C:\\x\\u{2029}y", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeText_ACitrixPathWithBidi_IsMadeVisible()
    {
        ServerProfileDto dto = Profile("CITRIX", d => d.CitrixIcaFilePath = "C:\\a\u202eb.ica");
        ServerDialogViewModel viewModel = ServerDialogViewModel.FromDto(dto);
        viewModel.Localizer = LoadLocalizer();

        string text = viewModel.ExecutionTrustNoticeText;

        Assert.DoesNotContain('\u202e', text);
        Assert.Contains("C:\\a\\u{202e}b.ica", text, StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static LocalizationManager LoadLocalizer()
    {
        LocalizationManager localizer = new();
        localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en").GetAwaiter().GetResult();
        return localizer;
    }

    private static PostConnectStep Step(string input, bool enabled = true)
        => new() { Id = Guid.NewGuid().ToString(), Input = input, Enabled = enabled };

    /// <summary>A profile as an importer leaves it: unconfirmed, with nothing else set.</summary>
    private static ServerProfileDto Profile(string connectionType, Action<ServerProfileDto> configure)
    {
        ServerProfileDto dto = new()
        {
            Id = "imported-1",
            DisplayName = "imported",
            ConnectionType = connectionType,
            RemoteServer = "elsewhere.example.com",
            ExecutionConfirmed = false,
        };
        configure(dto);
        return dto;
    }

    private static ServerProfileDto PayloadProfile(string kind) => kind switch
    {
        "local-executable" => Profile("LOCAL", d => d.LocalShellExecutable = "evil.exe"),
        "local-arguments" => Profile("LOCAL", d => d.LocalShellArguments = "-EncodedCommand AAAA"),
        "local-working-directory" => Profile("LOCAL", d => d.LocalShellWorkingDirectory = "C:\\evil\\dir"),
        "local-elevated" => Profile("LOCAL", d => d.ElevationMode = ElevationMode.Gsudo),
        "web-external" => Profile("WEB", d =>
        {
            d.WebOpenExternally = true;
            d.WebExternalProgram = "legacy.exe";
        }),
        "citrix-ica" => Profile("CITRIX", d => d.CitrixIcaFilePath = "C:\\evil\\launch.ica"),
        "ssh-step" => Profile("SSH", d => d.PostConnectSteps = [Step("rm -rf /")]),
        "rdp-step" => Profile("RDP", d => d.PostConnectSteps = [Step("calc.exe")]),
        "linked-step" => Profile("SSH", d => d.PostConnectSteps =
        [
            new PostConnectStep
            {
                Id = "linked-1",
                CommandLibraryId = "lib-1",
                CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["unit"] = "nginx",
                    ["mode"] = "fast",
                },
            },
        ]),
        "plain-rdp" => Profile("RDP", _ => { }),
        "plain-local" => Profile("LOCAL", _ => { }),
        "web-default-browser" => Profile("WEB", d => d.WebOpenExternally = true),
        "web-program-not-external" => Profile("WEB", d =>
        {
            d.WebOpenExternally = false;
            d.WebExternalProgram = "legacy.exe";
        }),
        "disabled-step" => Profile("SSH", d => d.PostConnectSteps = [Step("rm -rf /", enabled: false)]),
        "blank-step" => Profile("SSH", d => d.PostConnectSteps = [Step("   ")]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
