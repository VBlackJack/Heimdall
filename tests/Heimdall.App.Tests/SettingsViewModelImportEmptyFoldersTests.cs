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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The mRemoteNG import also creates the containers of the file that end up holding no session,
/// as empty folders in the file's order, and only for that file format.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private const string EmptyFoldersMRemoteNg = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
          <Node Name="Later" Type="Container" />
          <Node Name="Prod" Type="Container">
            <Node Name="web" Type="Connection" Hostname="web.example.com" Protocol="SSH2" />
            <Node Name="Spare" Type="Container" />
          </Node>
          <Node Name="Broken" Type="Container">
            <Node Name="bad" Type="Connection" Hostname="bad.example.com" Protocol="SSH2" Port="70000" />
          </Node>
          <Node Name="Alpha" Type="Container">
            <Node Name="db" Type="Connection" Hostname="db.example.com" Protocol="RDP" />
          </Node>
        </mrng:Connections>
        """;

    private const string FoldersOnlyMRemoteNg = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
          <Node Name="Later" Type="Container" />
          <Node Name="Next" Type="Container"><Node Name="Inner" Type="Container" /></Node>
        </mrng:Connections>
        """;

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_CreatesTheEmptyFoldersInFileOrder_InOneSettingsWrite()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };

        await ImportOrderFileAsync(config, dialog, "confCons.xml", EmptyFoldersMRemoteNg);

        Assert.Equal(["Later", "Prod/Spare", "Broken"], config.Settings.EmptyGroups);
        Assert.Equal(["Later", "Prod", "Broken", "Alpha"], config.Settings.FolderOrder);
        Assert.Equal(["servers", "settings"], config.PersistenceCalls);
        Assert.Equal(1, config.MergeSettingCallCount);
        Dictionary<string, ServerProfileDto> saved = ByName(config.SavedServers);
        Assert.Equal(["db", "web"], saved.Keys.Order());
        Assert.Equal("Prod", saved["web"].Group);
        Assert.Equal("Alpha", saved["db"].Group);

        (string _, string summary) = Assert.Single(dialog.InfoCalls);
        int sessionsLine = summary.IndexOf("Imported 2 sessions", StringComparison.Ordinal);
        int foldersLine = summary.IndexOf("3 empty folders created.", StringComparison.Ordinal);
        int warningLine = summary.IndexOf("bad", StringComparison.Ordinal);
        Assert.True(sessionsLine >= 0, summary);
        Assert.True(foldersLine > sessionsLine, summary);
        Assert.True(warningLine > foldersLine, summary);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_AnEmptyContainerAlreadyThereInAnotherCase_IsNotCreatedAgain()
    {
        FakeConfigManager config = new();
        config.Settings.EmptyGroups.Add("ops");
        FakeDialogService dialog = new() { ConfirmResult = true };
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
              <Node Name="OPS" Type="Container" />
              <Node Name="root-a" Type="Connection" Hostname="r.example.com" Protocol="RDP" />
            </mrng:Connections>
            """;

        await ImportOrderFileAsync(config, dialog, "confCons.xml", content);

        Assert.Equal(["ops"], config.Settings.EmptyGroups);
        Assert.Equal(["servers"], config.PersistenceCalls);
        (string _, string summary) = Assert.Single(dialog.InfoCalls);
        Assert.DoesNotContain("empty folder", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_AFileWithOnlyEmptyFolders_CreatesThemAfterOneQuestion()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };

        int inventoryChanges = await ImportOrderFileAsync(config, dialog, "confCons.xml", FoldersOnlyMRemoteNg);

        (string _, string question, string _) = Assert.Single(dialog.ConfirmCalls);
        Assert.Equal("Create 3 empty folders from this file? No session in it can be imported.", question);
        Assert.Equal(["settings"], config.PersistenceCalls);
        Assert.Null(config.SavedServers);
        Assert.Equal(["Later", "Next", "Next/Inner"], config.Settings.EmptyGroups);
        (string _, string summary) = Assert.Single(dialog.InfoCalls);
        Assert.Equal("3 empty folders created.", summary);
        Assert.Empty(dialog.ErrorCalls);
        Assert.Equal(1, inventoryChanges);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_AFileWithOnlyEmptyFolders_Declined_WritesNothing()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = false };

        int inventoryChanges = await ImportOrderFileAsync(config, dialog, "confCons.xml", FoldersOnlyMRemoteNg);

        Assert.Single(dialog.ConfirmCalls);
        Assert.Empty(config.PersistenceCalls);
        Assert.Empty(config.Settings.EmptyGroups);
        Assert.Empty(dialog.InfoCalls);
        Assert.Equal(0, inventoryChanges);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_AFileWithOnlyFoldersThatAlreadyExist_SaysNothingWasFound()
    {
        FakeConfigManager config = new()
        {
            Servers = [new ServerProfileDto { Id = "old", DisplayName = "old", Group = "next/inner" }],
        };
        config.Settings.EmptyGroups.Add("LATER");
        FakeDialogService dialog = new() { ConfirmResult = true };

        await ImportOrderFileAsync(config, dialog, "confCons.xml", FoldersOnlyMRemoteNg);

        Assert.Empty(dialog.ConfirmCalls);
        Assert.Empty(config.PersistenceCalls);
        Assert.Equal(["LATER"], config.Settings.EmptyGroups);
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        (string _, string shown) = Assert.Single(dialog.InfoCalls);
        Assert.Equal(localizer["ImportNoSessionsFound"], shown);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_AFileWithOnlyEmptyFolders_WhenTheWriteFails_ReportsTheError()
    {
        FakeConfigManager config = new() { FailOnMergeSetting = true };
        FakeDialogService dialog = new() { ConfirmResult = true };

        int inventoryChanges = await ImportOrderFileAsync(config, dialog, "confCons.xml", FoldersOnlyMRemoteNg);

        Assert.Single(dialog.ErrorCalls);
        Assert.Empty(dialog.InfoCalls);
        Assert.Equal(0, inventoryChanges);
        Assert.Empty(config.Settings.EmptyGroups);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_WithSessions_WhenTheFolderWriteFails_StillReportsTheImport()
    {
        FakeConfigManager config = new() { FailOnMergeSetting = true };
        FakeDialogService dialog = new() { ConfirmResult = true };

        int inventoryChanges = await ImportOrderFileAsync(config, dialog, "confCons.xml", EmptyFoldersMRemoteNg);

        Assert.Empty(dialog.ErrorCalls);
        (string _, string summary) = Assert.Single(dialog.InfoCalls);
        Assert.DoesNotContain("empty folders created", summary, StringComparison.Ordinal);
        Assert.Equal(1, inventoryChanges);
        Assert.Equal(2, config.SavedServers!.Count);
        Assert.Empty(config.Settings.EmptyGroups);
    }

    [Fact]
    public async Task ImportConfigCommand_RdcMan_AnEmptyGroupIsNotCreated()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <properties><name>Corp</name></properties>
                <group>
                  <properties><name>Empty</name></properties>
                </group>
                <group>
                  <properties><name>Alpha</name></properties>
                  <server><properties><name>db.example.com</name><displayName>db</displayName></properties></server>
                </group>
              </file>
            </RDCMan>
            """;

        await ImportOrderFileAsync(config, dialog, "servers.rdg", content);

        Assert.Single(config.SavedServers!);
        Assert.Empty(config.Settings.EmptyGroups);
        Assert.Equal(["servers"], config.PersistenceCalls);
    }

    private const string AllRefusedMRemoteNg = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
          <Node Name="Broken" Type="Container">
            <Node Name="bad" Type="Connection" Hostname="bad.example.com" Protocol="SSH2" Port="70000" />
          </Node>
        </mrng:Connections>
        """;

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_EverySessionRefused_Accepted_ShowsTheFolderAndTheWarning()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };

        await ImportOrderFileAsync(config, dialog, "confCons.xml", AllRefusedMRemoteNg);

        Assert.Equal(["Broken"], config.Settings.EmptyGroups);
        Assert.Equal(["settings"], config.PersistenceCalls);
        (string _, string summary) = Assert.Single(dialog.InfoCalls);
        int foldersLine = summary.IndexOf("1 empty folder created.", StringComparison.Ordinal);
        Assert.True(foldersLine >= 0, summary);
        Assert.True(summary.IndexOf("bad", StringComparison.Ordinal) > foldersLine, summary);
        Assert.DoesNotContain("Imported 0", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_EverySessionRefused_Declined_StillShowsTheWarning()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = false };

        await ImportOrderFileAsync(config, dialog, "confCons.xml", AllRefusedMRemoteNg);

        Assert.Empty(config.PersistenceCalls);
        Assert.Empty(config.Settings.EmptyGroups);
        Assert.Empty(dialog.InfoCalls);
        (string _, string warning) = Assert.Single(dialog.WarningCalls);
        Assert.Contains("bad", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_NewEmptyFolders_LandAfterTheExistingSiblingsInFileOrder()
    {
        FakeConfigManager config = new()
        {
            Servers =
            [
                new ServerProfileDto { Id = "a", DisplayName = "a", Group = "Alpha" },
                new ServerProfileDto { Id = "b", DisplayName = "b", Group = "Beta" },
                new ServerProfileDto { Id = "c", DisplayName = "c", Group = "Gamma" },
            ],
        };
        config.Settings.FolderOrder.AddRange(["Gamma", "Beta"]);
        FakeDialogService dialog = new() { ConfirmResult = true };
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
              <Node Name="Zed" Type="Container" />
              <Node Name="Mid" Type="Container" />
              <Node Name="root-a" Type="Connection" Hostname="r.example.com" Protocol="RDP" />
            </mrng:Connections>
            """;

        await ImportOrderFileAsync(config, dialog, "confCons.xml", content);

        Assert.Equal(["Gamma", "Beta", "Alpha", "Zed", "Mid"], config.Settings.FolderOrder);
        Assert.Equal(["Zed", "Mid"], config.Settings.EmptyGroups);
        Assert.Equal(["servers", "settings"], config.PersistenceCalls);
    }

    [Fact]
    public async Task ImportConfigCommand_RdcManGivenAsXml_IsStillReadAsRdcMan_AndCreatesNoFolder()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        // Nothing here matches the mRemoteNG detection (no "<Connections", no Type="Connection"
        // or Type="Container"), so the RDCMan parser decides the branch.
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <properties><name>Corp</name></properties>
                <group>
                  <properties><name>Empty</name></properties>
                </group>
                <group>
                  <properties><name>Alpha</name></properties>
                  <server><properties><name>db.example.com</name><displayName>db</displayName></properties></server>
                </group>
              </file>
            </RDCMan>
            """;

        await ImportOrderFileAsync(config, dialog, "servers.xml", content);

        Assert.Equal(Heimdall.Core.Models.ProfileOrigin.ImportRdcMan, Assert.Single(config.SavedServers!).Origin);
        Assert.Empty(config.Settings.EmptyGroups);
        Assert.Equal(["servers"], config.PersistenceCalls);
    }
}
