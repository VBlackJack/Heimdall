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
using System.Text.Json;
using Heimdall.App.Services.Import;
using Heimdall.Core.Configuration;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class CommandLibraryTransferServiceTests
{
    [Fact]
    public async Task ExportAsync_WritesFile_AndReturnsActionCount()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService(
        [
            CommandLibraryTestHelpers.CreateLinuxAction("a1", "Tail log", "tail -f /tmp/app.log"),
            CommandLibraryTestHelpers.CreateLinuxAction("a2", "List dir", "ls -la")
        ]);

        var path = Path.GetTempFileName();
        try
        {
            var count = await service.ExportAsync(actionService, path);

            Assert.Equal(2, count);
            Assert.True(File.Exists(path));
            Assert.Contains("\"actions\"", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAsync_BulkFormat_ImportsNewActions()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);

        var json = JsonSerializer.Serialize(new
        {
            actions = new[]
            {
                new { publicId = Guid.NewGuid(), id = "imp-1", title = "Alpha", category = "Net" },
                new { publicId = Guid.NewGuid(), id = "imp-2", title = "Beta", category = "Net" }
            }
        });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.Success, result.Outcome);
            Assert.Equal(2, result.Imported);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Skipped);
            Assert.Equal(2, (await actionService.GetAllActionsAsync()).Count());
        });
    }

    [Fact]
    public async Task ImportAsync_UpdatesExistingUserCreatedAction_MatchedByPublicId()
    {
        var publicId = Guid.NewGuid();
        var existing = new ActionModel
        {
            Id = "seed-1",
            PublicId = publicId,
            Title = "Old title",
            Category = "Cat",
            IsUserCreated = true
        };
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([existing]);

        var json = JsonSerializer.Serialize(new
        {
            actions = new[]
            {
                new { publicId, id = "ignored", title = "New title", category = "Cat" }
            }
        });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(1, result.Updated);
            Assert.Equal(0, result.Imported);
            Assert.Equal(0, result.Skipped);

            var stored = await actionService.GetActionByPublicIdAsync(publicId);
            Assert.NotNull(stored);
            Assert.Equal("New title", stored!.Title);
            Assert.Equal("seed-1", stored.Id);
            Assert.True(stored.IsUserCreated);
        });
    }

    [Fact]
    public async Task ImportAsync_SkipsExistingSystemAction_WithoutModifying()
    {
        var publicId = Guid.NewGuid();
        var existing = new ActionModel
        {
            Id = "sys-1",
            PublicId = publicId,
            Title = "System title",
            Category = "Cat",
            IsUserCreated = false
        };
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([existing]);

        var json = JsonSerializer.Serialize(new
        {
            actions = new[]
            {
                new { publicId, id = "sys-1", title = "Tampered", category = "Cat" }
            }
        });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(1, result.Skipped);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Imported);

            var stored = await actionService.GetActionByPublicIdAsync(publicId);
            Assert.NotNull(stored);
            Assert.Equal("System title", stored!.Title);
            Assert.False(stored.IsUserCreated);
        });
    }

    [Fact]
    public async Task ImportAsync_SkipsInvalidEntries()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);

        var json = JsonSerializer.Serialize(new
        {
            actions = new object[]
            {
                new { publicId = Guid.NewGuid(), id = "blank-title", title = "", category = "Cat" },
                new { publicId = Guid.NewGuid(), id = "blank-category", title = "Valid", category = "" },
                new
                {
                    publicId = Guid.NewGuid(),
                    id = "long-title",
                    title = new string('a', AppConstants.MaxImportActionTitleLength + 1),
                    category = "Cat"
                },
                new
                {
                    publicId = Guid.NewGuid(),
                    id = "long-category",
                    title = "Valid",
                    category = new string('b', AppConstants.MaxImportActionCategoryLength + 1)
                }
            }
        });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.Success, result.Outcome);
            Assert.Equal(4, result.Skipped);
            Assert.Equal(0, result.Imported);
            Assert.Equal(0, result.Updated);
            Assert.Empty(await actionService.GetAllActionsAsync());
        });
    }

    [Fact]
    public async Task ImportAsync_SingleActionFormat_ImportsOne()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);

        var json = JsonSerializer.Serialize(new
        {
            id = "single-1",
            publicId = Guid.NewGuid(),
            title = "Single action",
            category = "Net"
        });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.Success, result.Outcome);
            Assert.Equal(1, result.Imported);
            Assert.Single(await actionService.GetAllActionsAsync());
        });
    }

    [Fact]
    public async Task ImportAsync_InvalidJson_ReturnsInvalidFormat()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);

        await WithTempFileAsync("{ this is not valid json", async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.InvalidFormat, result.Outcome);
        });
    }

    [Fact]
    public async Task ImportAsync_FileExceedingLimit_ReturnsFileTooLarge()
    {
        var service = new CommandLibraryTransferService(maxImportFileSizeBytes: 1);
        var actionService = new FakeActionService([]);

        await WithTempFileAsync("{ \"actions\": [] }", async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.FileTooLarge, result.Outcome);
        });
    }

    private static async Task WithTempFileAsync(string content, Func<string, Task> body)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, content);
            await body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
