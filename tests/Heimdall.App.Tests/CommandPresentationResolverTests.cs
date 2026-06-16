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
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Producer-first tests for <see cref="CommandPresentationResolver.Resolve"/>.
/// The expected risk/platform keys and brush keys mirror the producer at
/// src/Heimdall.App/ViewModels/CommandLibrary/CommandLibraryActionEntry.cs
/// (RiskLabel/RiskBadge/RiskBrushKey/PlatformLabel switches). The identity
/// localizer (<c>key =&gt; key</c>) means localized assertions check the i18n KEY
/// while non-localized fields (brush key, passthroughs) check raw values.
/// </summary>
public sealed class CommandPresentationResolverTests
{
    private static readonly Func<string, string> Localize = key => key;

    [Theory]
    [InlineData(CriticalityLevel.Info, "ToolCmdLibRiskInfo", "ToolCmdLibRiskBadgeInfo", "TextSecondaryBrush")]
    [InlineData(CriticalityLevel.Run, "ToolCmdLibRiskRun", "ToolCmdLibRiskBadgeRun", "WarningBrush")]
    [InlineData(CriticalityLevel.Dangerous, "ToolCmdLibRiskDangerous", "ToolCmdLibRiskBadgeDanger", "ErrorBrush")]
    public void Resolve_RiskFields_MirrorActionEntrySwitch(
        CriticalityLevel level, string expectedLabel, string expectedBadge, string expectedBrush)
    {
        var action = new ActionModel { Level = level };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.Level.Should().Be(level);
        presentation.RiskLabel.Should().Be(expectedLabel);
        presentation.RiskBadge.Should().Be(expectedBadge);
        presentation.RiskBrushKey.Should().Be(expectedBrush);
    }

    [Theory]
    [InlineData(Platform.Windows, "ToolCmdLibPlatformLabelWin")]
    [InlineData(Platform.Linux, "ToolCmdLibPlatformLabelLin")]
    [InlineData(Platform.Both, "ToolCmdLibPlatformLabelBoth")]
    public void Resolve_PlatformLabel_MirrorsActionEntrySwitch(Platform platform, string expectedLabel)
    {
        var action = new ActionModel { Platform = platform };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.Platform.Should().Be(platform);
        presentation.PlatformLabel.Should().Be(expectedLabel);
    }

    [Fact]
    public void Resolve_NullDescription_YieldsEmptyString()
    {
        var action = new ActionModel { Description = null! };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.Description.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrWhitespaceNotes_YieldsEmptyString(string? notes)
    {
        var action = new ActionModel { Notes = notes };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.Notes.Should().BeEmpty();
    }

    [Fact]
    public void HasMetadata_IsFalse_WhenNoNotesExamplesOrLinks()
    {
        var action = new ActionModel();

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.HasMetadata.Should().BeFalse();
    }

    [Fact]
    public void HasMetadata_IsTrue_WhenNotesSet()
    {
        var action = new ActionModel { Notes = "Run as root." };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.HasMetadata.Should().BeTrue();
    }

    [Fact]
    public void HasMetadata_IsTrue_WhenExamplesNonEmpty()
    {
        var action = new ActionModel
        {
            Examples = { new CommandExample { Command = "df -h" } }
        };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.HasMetadata.Should().BeTrue();
    }

    [Fact]
    public void HasMetadata_IsTrue_WhenLinksNonEmpty()
    {
        var action = new ActionModel
        {
            Links = { new ExternalLink { Title = "Manual", Url = "https://example.test" } }
        };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.HasMetadata.Should().BeTrue();
    }

    [Fact]
    public void Resolve_PassesThroughTitleDescriptionNotesExamplesLinks()
    {
        var examples = new List<CommandExample> { new() { Command = "df -h" } };
        var links = new List<ExternalLink> { new() { Title = "Manual", Url = "https://example.test" } };
        var action = new ActionModel
        {
            Title = "Disk usage",
            Description = "Show free disk space",
            Notes = "Root recommended",
            Examples = examples,
            Links = links
        };

        var presentation = CommandPresentationResolver.Resolve(action, Localize);

        presentation.Title.Should().Be("Disk usage");
        presentation.Description.Should().Be("Show free disk space");
        presentation.Notes.Should().Be("Root recommended");
        presentation.Examples.Should().BeSameAs(examples);
        presentation.Links.Should().BeSameAs(links);
    }
}
