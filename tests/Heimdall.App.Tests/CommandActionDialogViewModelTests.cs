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

using FluentAssertions;
using Heimdall.App.ViewModels.Dialogs;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class CommandActionDialogViewModelTests
{
    private static ActionModel CreateActionWithExamplesAndLinks()
    {
        return new ActionModel
        {
            Id = "act-1",
            PublicId = Guid.NewGuid(),
            Title = "List files",
            Description = "List directory contents",
            Category = "Filesystem",
            Platform = Platform.Both,
            Level = CriticalityLevel.Info,
            Examples =
            {
                new CommandExample { Command = "ls", Description = "Generic", Platform = Platform.Both },
            },
            WindowsExamples =
            {
                new CommandExample { Command = "dir", Description = "Windows", Platform = Platform.Windows },
            },
            LinuxExamples =
            {
                new CommandExample { Command = "ls -la", Description = "Linux", Platform = Platform.Linux },
            },
            Links =
            {
                new ExternalLink { Title = "Docs", Url = "https://example.com/docs" },
            },
            WindowsCommandTemplate = new CommandTemplate
            {
                Id = "act-1-win",
                PublicId = Guid.NewGuid(),
                Platform = Platform.Windows,
                Name = "List files",
                CommandPattern = "dir",
            },
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = "act-1-linux",
                PublicId = Guid.NewGuid(),
                Platform = Platform.Linux,
                Name = "List files",
                CommandPattern = "ls -la",
            },
        };
    }

    [Fact]
    public void EditRoundTrip_PreservesExamplesAndLinks()
    {
        var source = CreateActionWithExamplesAndLinks();

        var vm = CommandActionDialogViewModel.FromAction(source);
        var result = vm.ToAction();

        result.Examples.Should().BeEquivalentTo(source.Examples);
        result.WindowsExamples.Should().BeEquivalentTo(source.WindowsExamples);
        result.LinuxExamples.Should().BeEquivalentTo(source.LinuxExamples);
        result.Links.Should().BeEquivalentTo(source.Links);
    }

    [Fact]
    public void EditRoundTrip_DoesNotAliasSourceLists()
    {
        var source = CreateActionWithExamplesAndLinks();
        var originalExampleCount = source.Examples.Count;
        var originalLinkCount = source.Links.Count;

        var vm = CommandActionDialogViewModel.FromAction(source);
        var result = vm.ToAction();

        // Mutating the returned action must not bleed back into the source.
        result.Examples.Add(new CommandExample { Command = "extra", Description = "x", Platform = Platform.Both });
        result.WindowsExamples.Add(new CommandExample { Command = "extra-win", Description = "x", Platform = Platform.Windows });
        result.LinuxExamples.Add(new CommandExample { Command = "extra-linux", Description = "x", Platform = Platform.Linux });
        result.Links.Add(new ExternalLink { Title = "Extra", Url = "https://example.com/extra" });

        source.Examples.Should().HaveCount(originalExampleCount);
        source.WindowsExamples.Should().HaveCount(1);
        source.LinuxExamples.Should().HaveCount(1);
        source.Links.Should().HaveCount(originalLinkCount);
    }

    [Fact]
    public void AddMode_ProducesNonNullEmptyCollections()
    {
        var vm = new CommandActionDialogViewModel
        {
            Title = "New action",
            Category = "Misc",
            LinuxPattern = "echo hi",
        };

        var result = vm.ToAction();

        result.Examples.Should().NotBeNull().And.BeEmpty();
        result.WindowsExamples.Should().NotBeNull().And.BeEmpty();
        result.LinuxExamples.Should().NotBeNull().And.BeEmpty();
        result.Links.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void EditingTitle_KeepsExamplesAndLinksIntact()
    {
        var source = CreateActionWithExamplesAndLinks();

        var vm = CommandActionDialogViewModel.FromAction(source);
        vm.Title = "Renamed action";
        var result = vm.ToAction();

        result.Title.Should().Be("Renamed action");
        result.Examples.Should().BeEquivalentTo(source.Examples);
        result.WindowsExamples.Should().BeEquivalentTo(source.WindowsExamples);
        result.LinuxExamples.Should().BeEquivalentTo(source.LinuxExamples);
        result.Links.Should().BeEquivalentTo(source.Links);
    }
}
