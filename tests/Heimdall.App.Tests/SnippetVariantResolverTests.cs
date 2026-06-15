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
using Heimdall.App.ViewModels.CommandPalette;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class SnippetVariantResolverTests
{
    private static CommandTemplate Template(Platform platform, string pattern) => new()
    {
        Platform = platform,
        Name = "template",
        CommandPattern = pattern
    };

    private static CommandExample Example(string command, Platform platform = Platform.Both) => new()
    {
        Command = command,
        Platform = platform
    };

    [Fact]
    public void GetVariants_BothTemplatesPresent_ReturnsWindowsThenLinux()
    {
        var action = new ActionModel
        {
            Title = "Cross platform",
            WindowsCommandTemplate = Template(Platform.Windows, "Get-Process"),
            LinuxCommandTemplate = Template(Platform.Linux, "ps aux")
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().HaveCount(2);
        variants[0].Kind.Should().Be(SnippetVariantKind.WindowsTemplate);
        variants[0].Platform.Should().Be(Platform.Windows);
        variants[0].ExampleIndex.Should().Be(-1);
        variants[0].Template.Should().BeSameAs(action.WindowsCommandTemplate);
        variants[0].LiteralCommand.Should().BeNull();
        variants[0].PreviewCommand.Should().Be("Get-Process");

        variants[1].Kind.Should().Be(SnippetVariantKind.LinuxTemplate);
        variants[1].Platform.Should().Be(Platform.Linux);
        variants[1].ExampleIndex.Should().Be(-1);
        variants[1].Template.Should().BeSameAs(action.LinuxCommandTemplate);
        variants[1].LiteralCommand.Should().BeNull();
        variants[1].PreviewCommand.Should().Be("ps aux");
    }

    [Fact]
    public void GetVariants_OnlyWindowsTemplate_ReturnsSingleWindowsVariant()
    {
        var action = new ActionModel
        {
            WindowsCommandTemplate = Template(Platform.Windows, "Get-Service")
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().ContainSingle();
        variants[0].Kind.Should().Be(SnippetVariantKind.WindowsTemplate);
        variants[0].PreviewCommand.Should().Be("Get-Service");
    }

    [Fact]
    public void GetVariants_OnlyLinuxTemplate_ReturnsSingleLinuxVariant()
    {
        var action = new ActionModel
        {
            LinuxCommandTemplate = Template(Platform.Linux, "systemctl status")
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().ContainSingle();
        variants[0].Kind.Should().Be(SnippetVariantKind.LinuxTemplate);
        variants[0].Platform.Should().Be(Platform.Linux);
        variants[0].PreviewCommand.Should().Be("systemctl status");
    }

    [Fact]
    public void GetVariants_WhitespaceTemplatePattern_IsSkipped()
    {
        var action = new ActionModel
        {
            WindowsCommandTemplate = Template(Platform.Windows, "   "),
            LinuxCommandTemplate = Template(Platform.Linux, "ls")
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().ContainSingle();
        variants[0].Kind.Should().Be(SnippetVariantKind.LinuxTemplate);
    }

    [Fact]
    public void GetVariants_ExamplesAcrossLists_AreTaggedWithPlatformAndIndex()
    {
        var action = new ActionModel
        {
            WindowsExamples = { Example("dir", Platform.Windows), Example("cls", Platform.Windows) },
            LinuxExamples = { Example("ls -la", Platform.Linux) },
            Examples = { Example("echo hi") }
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().HaveCount(4);

        variants[0].Kind.Should().Be(SnippetVariantKind.Example);
        variants[0].Platform.Should().Be(Platform.Windows);
        variants[0].ExampleIndex.Should().Be(0);
        variants[0].Template.Should().BeNull();
        variants[0].LiteralCommand.Should().Be("dir");
        variants[0].PreviewCommand.Should().Be("dir");

        variants[1].Platform.Should().Be(Platform.Windows);
        variants[1].ExampleIndex.Should().Be(1);
        variants[1].LiteralCommand.Should().Be("cls");

        variants[2].Platform.Should().Be(Platform.Linux);
        variants[2].ExampleIndex.Should().Be(0);
        variants[2].LiteralCommand.Should().Be("ls -la");

        variants[3].Platform.Should().BeNull();
        variants[3].ExampleIndex.Should().Be(0);
        variants[3].LiteralCommand.Should().Be("echo hi");
    }

    [Fact]
    public void GetVariants_OrdersTemplatesBeforeExamples()
    {
        var action = new ActionModel
        {
            WindowsCommandTemplate = Template(Platform.Windows, "Get-Process"),
            LinuxCommandTemplate = Template(Platform.Linux, "ps aux"),
            Examples = { Example("echo hi") }
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().HaveCount(3);
        variants[0].Kind.Should().Be(SnippetVariantKind.WindowsTemplate);
        variants[1].Kind.Should().Be(SnippetVariantKind.LinuxTemplate);
        variants[2].Kind.Should().Be(SnippetVariantKind.Example);
    }

    [Fact]
    public void GetVariants_WhitespaceExampleCommands_AreSkipped()
    {
        var action = new ActionModel
        {
            Examples = { Example(""), Example("   "), Example("real") }
        };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().ContainSingle();
        variants[0].LiteralCommand.Should().Be("real");
        variants[0].ExampleIndex.Should().Be(2);
    }

    [Fact]
    public void GetVariants_NullAction_ReturnsEmpty()
    {
        var variants = SnippetVariantResolver.GetVariants(null);

        variants.Should().BeEmpty();
    }

    [Fact]
    public void GetVariants_ActionWithNoCommand_ReturnsEmpty()
    {
        var action = new ActionModel { Title = "No command" };

        var variants = SnippetVariantResolver.GetVariants(action);

        variants.Should().BeEmpty();
    }

    [Fact]
    public void GetDefault_NullAction_ReturnsNull()
    {
        SnippetVariantResolver.GetDefault(null).Should().BeNull();
    }

    [Fact]
    public void GetDefault_ActionWithNoCommand_ReturnsNull()
    {
        var action = new ActionModel { Title = "No command" };

        SnippetVariantResolver.GetDefault(action).Should().BeNull();
    }

    [Fact]
    public void GetDefault_PrefersWindowsTemplateOverLinuxAndExample()
    {
        var action = new ActionModel
        {
            WindowsCommandTemplate = Template(Platform.Windows, "Get-Process"),
            LinuxCommandTemplate = Template(Platform.Linux, "ps aux"),
            Examples = { Example("echo hi") }
        };

        var def = SnippetVariantResolver.GetDefault(action);

        def.Should().NotBeNull();
        def!.Kind.Should().Be(SnippetVariantKind.WindowsTemplate);
        def.PreviewCommand.Should().Be("Get-Process");
    }

    [Fact]
    public void GetDefault_PrefersLinuxTemplateOverExample()
    {
        var action = new ActionModel
        {
            LinuxCommandTemplate = Template(Platform.Linux, "ps aux"),
            Examples = { Example("echo hi") }
        };

        var def = SnippetVariantResolver.GetDefault(action);

        def.Should().NotBeNull();
        def!.Kind.Should().Be(SnippetVariantKind.LinuxTemplate);
    }

    [Fact]
    public void GetDefault_FallsBackToFirstExample()
    {
        var action = new ActionModel
        {
            Examples = { Example("   "), Example("echo hi") }
        };

        var def = SnippetVariantResolver.GetDefault(action);

        def.Should().NotBeNull();
        def!.Kind.Should().Be(SnippetVariantKind.Example);
        def.LiteralCommand.Should().Be("echo hi");
    }
}
