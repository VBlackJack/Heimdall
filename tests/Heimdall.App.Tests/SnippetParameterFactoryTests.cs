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
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

public sealed class SnippetParameterFactoryTests
{
    private static CommandTemplate TemplateWith(params TemplateParameter[] parameters) => new()
    {
        Name = "template",
        CommandPattern = "do {x}",
        Parameters = parameters.ToList()
    };

    [Fact]
    public void Build_NonHostParameter_UsesDefaultValue()
    {
        var template = TemplateWith(new TemplateParameter
        {
            Name = "path",
            Label = "Path",
            Type = "string",
            DefaultValue = "/tmp",
            Required = true,
            Description = "A path"
        });

        var entries = SnippetParameterFactory.Build(template, "host01");

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.Name.Should().Be("path");
        entry.Label.Should().Be("Path");
        entry.Required.Should().BeTrue();
        entry.Description.Should().Be("A path");
        entry.Type.Should().Be("string");
        entry.Value.Should().Be("/tmp");
    }

    [Fact]
    public void Build_NonHostParameterNoDefault_UsesEmpty()
    {
        var template = TemplateWith(new TemplateParameter { Name = "path", Type = "string" });

        var entries = SnippetParameterFactory.Build(template, "host01");

        entries[0].Value.Should().BeEmpty();
    }

    [Fact]
    public void Build_HostnameTypedParameter_PrefillsWithTargetHost()
    {
        var template = TemplateWith(new TemplateParameter
        {
            Name = "endpoint",
            Type = "hostname",
            DefaultValue = "fallback"
        });

        var entries = SnippetParameterFactory.Build(template, "host01");

        entries[0].Value.Should().Be("host01");
    }

    [Fact]
    public void Build_IpAddressTypedParameter_PrefillsOnlyWhenTargetHostIsValidIp()
    {
        var template = TemplateWith(new TemplateParameter
        {
            Name = "addr",
            Type = "ipaddress",
            DefaultValue = "0.0.0.0"
        });

        SnippetParameterFactory.Build(template, "10.0.0.5")[0].Value.Should().Be("10.0.0.5");
        // Not a valid IP -> falls back to the default value.
        SnippetParameterFactory.Build(template, "not-an-ip")[0].Value.Should().Be("0.0.0.0");
    }

    [Fact]
    public void Build_NameBasedHostParameter_PrefillsWithTargetHost()
    {
        var template = TemplateWith(new TemplateParameter
        {
            Name = "server",
            Type = "string",
            DefaultValue = "fallback"
        });

        var entries = SnippetParameterFactory.Build(template, "host01");

        entries[0].Value.Should().Be("host01");
    }

    [Fact]
    public void Build_NullTargetHost_DoesNotPrefill()
    {
        var template = TemplateWith(new TemplateParameter
        {
            Name = "hostname",
            Type = "hostname",
            DefaultValue = "fallback"
        });

        var entries = SnippetParameterFactory.Build(template, null);

        entries[0].Value.Should().Be("fallback");
    }

    [Fact]
    public void Build_WhitespaceTargetHost_DoesNotPrefill()
    {
        var template = TemplateWith(new TemplateParameter
        {
            Name = "hostname",
            Type = "hostname"
        });

        var entries = SnippetParameterFactory.Build(template, "   ");

        entries[0].Value.Should().BeEmpty();
    }

    [Fact]
    public void Build_NullParameters_ReturnsEmptyList()
    {
        var template = new CommandTemplate { Name = "t", CommandPattern = "noop", Parameters = null! };

        var entries = SnippetParameterFactory.Build(template, "host01");

        entries.Should().BeEmpty();
    }

    [Fact]
    public void Build_NoParameters_ReturnsEmptyList()
    {
        var template = TemplateWith();

        var entries = SnippetParameterFactory.Build(template, "host01");

        entries.Should().BeEmpty();
    }
}
