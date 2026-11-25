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

using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

/// <summary>
/// A saved tool entry whose tool is switched off is only dimmed in the tree: the file keeps it, so
/// turning the pack back on brings it back unchanged.
/// </summary>
public sealed class SavedToolEntryPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Heimdall.ToolEntry.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task ASavedToolEntry_SurvivesALoadAndSaveOfTheInventoryUnchanged()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        ConfigManager manager = new(_root);
        List<ServerProfileDto> original =
        [
            new()
            {
                Id = "tool-entry",
                DisplayName = "Port scanner",
                ConnectionType = ConnectionTypeCatalog.ToolPrefix + "PORTSCAN",
                Group = "ops",
            },
            new()
            {
                Id = "ssh-entry",
                DisplayName = "Gateway",
                RemoteServer = "gw.example.test",
                ConnectionType = "SSH",
                Group = "ops",
            },
        ];
        await manager.SaveServersAsync(original);
        byte[] firstBytes = await File.ReadAllBytesAsync(manager.ServersPath);

        List<ServerProfileDto> loaded = await manager.LoadServersAsync();
        await manager.SaveServersAsync(loaded);
        byte[] secondBytes = await File.ReadAllBytesAsync(manager.ServersPath);
        List<ServerProfileDto> reloaded = await manager.LoadServersAsync();

        Assert.Equal(firstBytes, secondBytes);
        ServerProfileDto tool = Assert.Single(reloaded, entry => entry.Id == "tool-entry");
        Assert.Equal(ConnectionTypeCatalog.ToolPrefix + "PORTSCAN", tool.ConnectionType);
        Assert.Equal("Port scanner", tool.DisplayName);
        Assert.Equal("ops", tool.Group);
        Assert.Equal(2, reloaded.Count);
    }
}
