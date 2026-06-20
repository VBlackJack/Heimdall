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

    [Fact]
    public void FromAction_FlattensBucketsIntoExamplesWithCorrectPlatform()
    {
        var source = CreateActionWithExamplesAndLinks();

        var vm = CommandActionDialogViewModel.FromAction(source);

        vm.Examples.Should().HaveCount(3);
        vm.Examples.Should().ContainSingle(e => e.Command == "ls" && e.Platform == Platform.Both);
        vm.Examples.Should().ContainSingle(e => e.Command == "dir" && e.Platform == Platform.Windows);
        vm.Examples.Should().ContainSingle(e => e.Command == "ls -la" && e.Platform == Platform.Linux);
        vm.Links.Should().ContainSingle(l => l.Title == "Docs" && l.Url == "https://example.com/docs");
    }

    [Fact]
    public void ToAction_RepartitionsWhenRowPlatformChanged()
    {
        var source = CreateActionWithExamplesAndLinks();
        var vm = CommandActionDialogViewModel.FromAction(source);

        // The "ls" example came from the generic (Both) bucket; retarget it to Linux.
        var generic = vm.Examples.Single(e => e.Command == "ls");
        generic.Platform = Platform.Linux;

        var result = vm.ToAction();

        result.Examples.Should().NotContain(e => e.Command == "ls");        // left the Both bucket
        result.LinuxExamples.Should().Contain(e => e.Command == "ls");      // moved into the Linux bucket
        result.LinuxExamples.Should().Contain(e => e.Command == "ls -la");  // original Linux example intact
    }

    [Fact]
    public void AddMode_ExampleWithWindowsPlatform_LandsInWindowsBucket()
    {
        var vm = new CommandActionDialogViewModel
        {
            Title = "New action",
            Category = "Misc",
            WindowsPattern = "dir",
        };
        vm.Examples.Add(new ExampleEntryVm { Command = "dir /a", Description = "All", Platform = Platform.Windows });
        vm.Examples.Add(new ExampleEntryVm { Command = "echo hi", Description = "Generic", Platform = Platform.Both });

        var result = vm.ToAction();

        result.WindowsExamples.Should().ContainSingle(e => e.Command == "dir /a" && e.Description == "All");
        result.Examples.Should().ContainSingle(e => e.Command == "echo hi");
        result.LinuxExamples.Should().BeEmpty();
    }

    [Fact]
    public void Links_RoundTripAndAdd()
    {
        var source = CreateActionWithExamplesAndLinks();
        var vm = CommandActionDialogViewModel.FromAction(source);
        vm.Links.Add(new LinkEntryVm { Title = "More", Url = "https://example.com/more" });

        var result = vm.ToAction();

        result.Links.Should().HaveCount(2);
        result.Links.Should().ContainSingle(l => l.Title == "Docs" && l.Url == "https://example.com/docs");
        result.Links.Should().ContainSingle(l => l.Title == "More" && l.Url == "https://example.com/more");
    }

    [Fact]
    public void AddExampleAndAddLink_MarkDialogDirty()
    {
        var vm = new CommandActionDialogViewModel();
        vm.IsDirty.Should().BeFalse();

        vm.AddExampleCommand.Execute(null);
        vm.IsDirty.Should().BeTrue();

        vm.IsDirty = false;
        vm.AddLinkCommand.Execute(null);
        vm.IsDirty.Should().BeTrue();
    }

    /// <summary>
    /// The dialog binds the Windows/Linux template sections' visibility directly to
    /// <see cref="CommandActionDialogViewModel.ShowWindowsSection"/> and
    /// <see cref="CommandActionDialogViewModel.ShowLinuxSection"/>, so a Platform change
    /// must raise PropertyChanged for both flags and return the correct value:
    /// Windows visible for Windows/Both, Linux visible for Linux/Both.
    /// </summary>
    [Theory]
    [InlineData(Platform.Windows, true, false)]
    [InlineData(Platform.Linux, false, true)]
    [InlineData(Platform.Both, true, true)]
    public void PlatformChange_NotifiesSectionFlags_WithCorrectValues(
        Platform platform,
        bool expectedWindows,
        bool expectedLinux)
    {
        // Start from a platform different to the target so the change actually fires.
        var vm = new CommandActionDialogViewModel { Platform = Platform.Windows };
        if (platform == Platform.Windows)
        {
            vm.Platform = Platform.Linux;
        }

        var notified = new List<string?>();
        vm.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        vm.Platform = platform;

        notified.Should().Contain(nameof(CommandActionDialogViewModel.ShowWindowsSection));
        notified.Should().Contain(nameof(CommandActionDialogViewModel.ShowLinuxSection));
        vm.ShowWindowsSection.Should().Be(expectedWindows);
        vm.ShowLinuxSection.Should().Be(expectedLinux);
    }

    /// <summary>
    /// Freezes the validation-localization convention: every DataAnnotation
    /// surfaced through the Title/Category error fields must resolve through the
    /// internal key map to its localized string. If an attribute's
    /// <c>ErrorMessage</c> is changed without updating the map, the error would
    /// fall back to the raw English text and no longer equal the localized value.
    /// </summary>
    [Fact]
    public async Task Validate_LocalizesEachSurfacedDataAnnotationError()
    {
        var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();

        var titleRequired = new CommandActionDialogViewModel
        {
            Localizer = localizer,
            Title = "",
            Category = "Ops",
            LinuxPattern = "ls",
        };
        titleRequired.ValidateCommand.Execute(null);
        titleRequired.TitleError.Should().Be(localizer["ToolCmdLibValidationTitleRequired"]);

        var titleTooLong = new CommandActionDialogViewModel
        {
            Localizer = localizer,
            Title = new string('x', 201),
            Category = "Ops",
            LinuxPattern = "ls",
        };
        titleTooLong.ValidateCommand.Execute(null);
        titleTooLong.TitleError.Should().Be(localizer["ToolCmdLibValidationTitleMaxLength"]);

        var categoryRequired = new CommandActionDialogViewModel
        {
            Localizer = localizer,
            Title = "Valid title",
            Category = "",
            LinuxPattern = "ls",
        };
        categoryRequired.ValidateCommand.Execute(null);
        categoryRequired.CategoryError.Should().Be(localizer["ToolCmdLibValidationCategoryRequired"]);
    }
}
