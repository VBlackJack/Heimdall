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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The legacy import keeps the order of an mRemoteNG file, and only of that file format.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private const string OrderSampleMRemoteNg = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
          <Node Name="Zeta" Type="Container">
            <Node Name="web-b" Type="Connection" Hostname="b.example.com" Protocol="SSH2" />
            <Node Name="Sub2" Type="Container"><Node Name="x" Type="Connection" Hostname="x.example.com" Protocol="SSH2" /></Node>
            <Node Name="Sub1" Type="Container"><Node Name="y" Type="Connection" Hostname="y.example.com" Protocol="SSH2" /></Node>
            <Node Name="web-a" Type="Connection" Hostname="a.example.com" Protocol="SSH2" />
          </Node>
          <Node Name="Alpha" Type="Container"><Node Name="db" Type="Connection" Hostname="db.example.com" Protocol="RDP" /></Node>
          <Node Name="root-z" Type="Connection" Hostname="z.example.com" Protocol="RDP" />
          <Node Name="root-a" Type="Connection" Hostname="r.example.com" Protocol="RDP" />
        </mrng:Connections>
        """;

    private static async Task<int> ImportOrderFileAsync(
        FakeConfigManager config,
        FakeDialogService dialog,
        string fileName,
        string content)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        int inventoryChanges = 0;
        viewModel.ServerInventoryChanged += () => inventoryChanges++;
        string importRoot = Path.Combine(
            Path.GetTempPath(),
            "heimdall-settings-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(importRoot);
        string importPath = Path.Combine(importRoot, fileName);

        try
        {
            await File.WriteAllTextAsync(importPath, content);
            viewModel.ImportFilePathProvider = () => importPath;

            await viewModel.ImportConfigCommand.ExecuteAsync(null);
        }
        finally
        {
            if (Directory.Exists(importRoot))
            {
                Directory.Delete(importRoot, recursive: true);
            }
        }

        return inventoryChanges;
    }

    private static Dictionary<string, ServerProfileDto> ByName(IEnumerable<ServerProfileDto>? servers) =>
        servers!.ToDictionary(server => server.DisplayName, StringComparer.Ordinal);

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_KeepsTheSourceOrderOfFoldersAndSessions()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };

        await ImportOrderFileAsync(config, dialog, "confCons.xml", OrderSampleMRemoteNg);

        Dictionary<string, ServerProfileDto> saved = ByName(config.SavedServers);
        Assert.Equal(10, saved["web-b"].SortOrder);
        Assert.Equal(20, saved["web-a"].SortOrder);
        Assert.Equal(10, saved["root-z"].SortOrder);
        Assert.Equal(20, saved["root-a"].SortOrder);
        Assert.Equal(0, saved["x"].SortOrder);
        Assert.Equal(0, saved["y"].SortOrder);
        Assert.Equal(0, saved["db"].SortOrder);
        Assert.Equal(["Zeta", "Alpha", "Zeta/Sub2", "Zeta/Sub1"], config.Settings.FolderOrder);
        Assert.Equal(["servers", "settings"], config.PersistenceCalls);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_IntoAnExistingFolder_AppendsAfterTheExistingSessions()
    {
        FakeConfigManager config = new()
        {
            Servers =
            [
                new ServerProfileDto { Id = "old-m", DisplayName = "m", Group = "Ops" },
                new ServerProfileDto { Id = "old-c", DisplayName = "c", Group = "Ops" },
            ]
        };
        FakeDialogService dialog = new() { ConfirmResult = true };
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
              <Node Name="Ops" Type="Container">
                <Node Name="z" Type="Connection" Hostname="z.example.com" Protocol="SSH2" />
                <Node Name="a" Type="Connection" Hostname="a.example.com" Protocol="SSH2" />
              </Node>
            </mrng:Connections>
            """;

        await ImportOrderFileAsync(config, dialog, "confCons.xml", content);

        Dictionary<string, ServerProfileDto> saved = ByName(config.SavedServers);
        Assert.Equal(10, saved["c"].SortOrder);
        Assert.Equal(20, saved["m"].SortOrder);
        Assert.Equal(30, saved["z"].SortOrder);
        Assert.Equal(40, saved["a"].SortOrder);
        Assert.Empty(config.Settings.FolderOrder);
        Assert.Equal(["servers"], config.PersistenceCalls);
    }

    [Fact]
    public async Task ImportConfigCommand_MRemoteNg_FolderOrderWriteFails_StillReportsTheImport()
    {
        FakeConfigManager config = new() { FailOnMergeSetting = true };
        FakeDialogService dialog = new() { ConfirmResult = true };

        int inventoryChanges = await ImportOrderFileAsync(config, dialog, "confCons.xml", OrderSampleMRemoteNg);

        Assert.Empty(dialog.ErrorCalls);
        Assert.Single(dialog.InfoCalls);
        Assert.Equal(1, inventoryChanges);
        Assert.Equal(7, config.SavedServers!.Count);
        Assert.Empty(config.Settings.FolderOrder);
    }

    [Fact]
    public async Task ImportConfigCommand_RdcMan_LeavesOrderUntouched()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <properties><name>Corp</name></properties>
                <group>
                  <properties><name>Zeta</name></properties>
                  <server><properties><name>b.example.com</name><displayName>web-b</displayName></properties></server>
                  <server><properties><name>a.example.com</name><displayName>web-a</displayName></properties></server>
                </group>
                <group>
                  <properties><name>Alpha</name></properties>
                  <server><properties><name>db.example.com</name><displayName>db</displayName></properties></server>
                </group>
              </file>
            </RDCMan>
            """;

        await ImportOrderFileAsync(config, dialog, "servers.rdg", content);

        Assert.Equal(3, config.SavedServers!.Count);
        Assert.All(config.SavedServers, server => Assert.Equal(0, server.SortOrder));
        Assert.Empty(config.Settings.FolderOrder);
        Assert.Equal(["servers"], config.PersistenceCalls);
    }
}
