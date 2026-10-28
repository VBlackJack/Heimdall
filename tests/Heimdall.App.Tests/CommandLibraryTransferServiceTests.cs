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
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
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
            var count = await service.ExportAsync((await actionService.GetAllActionsAsync()).ToList(), path);

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

    // A-05: an imported action keeps at most MaxActionExamplesCount examples per list. It is
    // truncated, not refused, and the dropped examples are counted in the result.
    [Fact]
    public async Task ImportAsync_ExampleListsLongerThanTheBound_AreTruncatedAndCounted()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);
        static object[] Examples(int count, int platform) =>
            Enumerable.Range(1, count)
                .Select(index => (object)new { command = $"cmd-{index}", description = $"example {index}", platform })
                .ToArray();

        var json = JsonSerializer.Serialize(new
        {
            actions = new[]
            {
                new
                {
                    publicId = Guid.NewGuid(),
                    id = "imp-long",
                    title = "Long",
                    category = "Net",
                    examples = Examples(13, 1),
                    windowsExamples = Examples(TwinShell.Core.Constants.ValidationConstants.MaxActionExamplesCount, 0),
                    linuxExamples = Examples(11, 1),
                }
            }
        });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(CommandLibraryImportOutcome.Success, result.Outcome);
            Assert.Equal(1, result.Imported);
            Assert.Equal(4, result.TruncatedExamples);

            var stored = Assert.Single(await actionService.GetAllActionsAsync());
            Assert.Equal(
                Enumerable.Range(1, 10).Select(index => $"cmd-{index}"),
                stored.Examples.Select(example => example.Command));
            Assert.Equal(10, stored.WindowsExamples.Count);
            Assert.Equal(10, stored.LinuxExamples.Count);
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

    [Fact]
    public async Task ImportAsync_ResetsInlineQuoting_WhenPlaceholderOutsideSingleQuotedSpan()
    {
        var publicId = Guid.NewGuid();
        var action = new ActionModel
        {
            Id = "quote-1",
            PublicId = publicId,
            Title = "Quoting",
            Category = "Net",
            WindowsCommandTemplate = new CommandTemplate
            {
                Id = "quote-1-win",
                Name = "win",
                Platform = Platform.Windows,
                CommandPattern = "Write-Output '{inside}' {outside}",
                Parameters =
                [
                    InlineParameter("inside"),
                    InlineParameter("outside")
                ]
            },
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = "quote-1-lin",
                Name = "lin",
                Platform = Platform.Linux,
                CommandPattern = "grep '{inside}' {outside}",
                Parameters =
                [
                    InlineParameter("inside"),
                    InlineParameter("outside")
                ]
            }
        };

        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);
        var json = JsonSerializer.Serialize(new { actions = new[] { action } });

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);
            Assert.Equal(1, result.Imported);

            var stored = await actionService.GetActionByPublicIdAsync(publicId);
            Assert.NotNull(stored);

            var win = stored!.WindowsCommandTemplate!.Parameters;
            Assert.Equal(QuotingMode.InlineInQuotes, FindParameter(win, "inside").Quoting);
            Assert.Equal(QuotingMode.ShellQuote, FindParameter(win, "outside").Quoting);

            var lin = stored.LinuxCommandTemplate!.Parameters;
            Assert.Equal(QuotingMode.InlineInQuotes, FindParameter(lin, "inside").Quoting);
            Assert.Equal(QuotingMode.ShellQuote, FindParameter(lin, "outside").Quoting);
        });
    }

    /// <summary>
    /// The import door used to count the single quotes before the first occurrence, which
    /// calls <c>'*{x}*'</c> single-quoted although the double-quoted string around it makes
    /// the shell expand <c>$(...)</c> there, and never looked at a second occurrence. It
    /// now reads the generator's lexer: inline only when every occurrence is literal.
    /// </summary>
    [Theory]
    [InlineData(Platform.Windows, "Get-ADUser -Filter \"Name -like '*{x}*'\"")]
    [InlineData(Platform.Linux, "sh -c \"grep '{x}' /etc/hosts\"")]
    [InlineData(Platform.Linux, "grep '{x}' /etc/hosts && echo {x}")]
    public async Task ImportAsync_InlinePlaceholderTheShellStillReads_IsRewrittenToShellQuote(Platform platform, string pattern)
    {
        var stored = await ImportSingleTemplateAsync(platform, pattern);

        Assert.Equal(QuotingMode.ShellQuote, FindParameter(stored.Parameters, "x").Quoting);
    }

    [Theory]
    [InlineData(Platform.Windows, "Get-ADUser -Filter '{x}'")]
    [InlineData(Platform.Linux, "grep '{x}' /etc/hosts | grep -v '{x}.bak'")]
    public async Task ImportAsync_InlinePlaceholderOnlyInSingleQuotes_KeepsInlineInQuotes(Platform platform, string pattern)
    {
        var stored = await ImportSingleTemplateAsync(platform, pattern);

        Assert.Equal(QuotingMode.InlineInQuotes, FindParameter(stored.Parameters, "x").Quoting);
    }

    [Fact]
    public async Task ExportThenImport_KeepsTheFlagAParameterDeclares()
    {
        var service = new CommandLibraryTransferService();
        var action = CommandLibraryTestHelpers.CreateLinuxAction(
            "flag-1",
            "Flagged",
            "tail -n {count} {file}",
            new TemplateParameter { Name = "count", Label = "Count", Type = "int", Flag = "-n" },
            CommandLibraryTestHelpers.RequiredParameter("file", "File"));
        var imported = new FakeActionService([]);
        var path = Path.GetTempFileName();

        try
        {
            await service.ExportAsync([action], path);
            Assert.Contains("\"flag\": \"-n\"", await File.ReadAllTextAsync(path));

            var result = await service.ImportAsync(imported, path);

            Assert.Equal(1, result.Imported);
            var saved = (await imported.GetAllActionsAsync()).Single();
            var parameters = saved.LinuxCommandTemplate!.Parameters;
            Assert.Equal("-n", FindParameter(parameters, "count").Flag);
            Assert.Null(FindParameter(parameters, "file").Flag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ImportAsync_FileWrittenBeforeTheFlagExisted_LeavesItNull()
    {
        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);
        var json = """
            { "actions": [ { "id": "old-1", "publicId": "11111111-1111-4111-8111-111111111111", "title": "Old", "category": "Net",
              "linuxCommandTemplate": { "id": "old-1-linux", "name": "Old", "platform": "linux", "commandPattern": "tail -n {count}",
                "parameters": [ { "name": "count", "label": "Count", "type": "int" } ] } } ] }
            """;

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);

            Assert.Equal(1, result.Imported);
            var saved = (await actionService.GetAllActionsAsync()).Single();
            Assert.Null(FindParameter(saved.LinuxCommandTemplate!.Parameters, "count").Flag);
        });
    }

    private static async Task<CommandTemplate> ImportSingleTemplateAsync(Platform platform, string pattern)
    {
        var publicId = Guid.NewGuid();
        var template = new CommandTemplate
        {
            Id = "lexer-tpl",
            Name = "lexer",
            Platform = platform,
            CommandPattern = pattern,
            Parameters = [InlineParameter("x")]
        };
        var action = new ActionModel
        {
            Id = "lexer-1",
            PublicId = publicId,
            Title = "Lexer",
            Category = "Net",
            WindowsCommandTemplate = platform == Platform.Windows ? template : null,
            LinuxCommandTemplate = platform == Platform.Linux ? template : null,
        };

        var service = new CommandLibraryTransferService();
        var actionService = new FakeActionService([]);
        var json = JsonSerializer.Serialize(new { actions = new[] { action } });
        CommandTemplate? stored = null;

        await WithTempFileAsync(json, async path =>
        {
            var result = await service.ImportAsync(actionService, path);
            Assert.Equal(1, result.Imported);
            var saved = await actionService.GetActionByPublicIdAsync(publicId);
            stored = platform == Platform.Windows ? saved!.WindowsCommandTemplate : saved!.LinuxCommandTemplate;
        });

        return stored!;
    }

    private static TemplateParameter InlineParameter(string name)
        => new()
        {
            Name = name,
            Label = name,
            Type = "string",
            Quoting = QuotingMode.InlineInQuotes
        };

    private static TemplateParameter FindParameter(IEnumerable<TemplateParameter> parameters, string name)
        => parameters.Single(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));

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
