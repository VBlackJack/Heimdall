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

using Heimdall.App.Services.PostConnect;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class CommandLibraryStepResolverTests
{
    [Fact]
    public async Task ResolveAsync_LiteralStep_ReturnsLiteralInput()
    {
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider();
        var resolver = new CommandLibraryStepResolver(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new PostConnectStep { Input = "pwd" }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.Literal, result.Status);
        Assert.Equal("pwd", result.ResolvedInput);
        Assert.Null(result.ReasonKey);
    }

    [Fact]
    public async Task ResolveAsync_MissingAction_ReturnsBrokenMissing()
    {
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider();
        var resolver = new CommandLibraryStepResolver(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new PostConnectStep
        {
            CommandLibraryId = "missing-action"
        }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.BrokenMissing, result.Status);
        Assert.Equal("LogPostConnectResolveBrokenMissing", result.ReasonKey);
        Assert.Null(result.ResolvedInput);
    }

    [Fact]
    public async Task ResolveAsync_NoTemplateAtAll_ReturnsBrokenNoTemplate()
    {
        var action = new ActionModel
        {
            Id = "no-template",
            Title = "No template",
            Category = "Ops",
            Platform = Platform.Both
        };
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(action);
        var resolver = new CommandLibraryStepResolver(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new PostConnectStep
        {
            CommandLibraryId = action.Id
        }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.BrokenNoTemplate, result.Status);
        Assert.Equal("LogPostConnectResolveBrokenNoTemplate", result.ReasonKey);
    }

    [Fact]
    public async Task ResolveAsync_WindowsOnlyAction_FallsBackToWindowsTemplate()
    {
        // SSH into a Windows OpenSSH host: no Linux template exists, but the
        // action's Windows pattern must still resolve instead of breaking.
        var action = new ActionModel
        {
            Id = "win-only",
            Title = "Windows only",
            Category = "Ops",
            Platform = Platform.Windows,
            WindowsCommandTemplate = new CommandTemplate
            {
                Id = "win-only-template",
                Name = "Windows only",
                Platform = Platform.Windows,
                CommandPattern = "Write-Host {name}",
                Parameters = [CommandLibraryTestHelpers.RequiredParameter("name", "Name")]
            }
        };
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(action);
        var resolver = new CommandLibraryStepResolver(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new PostConnectStep
        {
            CommandLibraryId = action.Id,
            CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["name"] = "hello"
            }
        }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.Resolved, result.Status);
        Assert.NotNull(result.ResolvedInput);
        Assert.Contains("hello", result.ResolvedInput!, StringComparison.Ordinal);
        Assert.Null(result.ReasonKey);
    }

    [Fact]
    public async Task ResolveAsync_InvalidParameters_ReturnsBrokenInvalidParams()
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction(
            "service-restart",
            "Restart service",
            "systemctl restart {service}",
            CommandLibraryTestHelpers.RequiredParameter("service", "Service"));
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(action);
        var resolver = new CommandLibraryStepResolver(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new PostConnectStep
        {
            CommandLibraryId = action.Id,
            CommandLibraryParams = []
        }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.BrokenInvalidParams, result.Status);
        Assert.Equal("LogPostConnectResolveBrokenInvalidParams", result.ReasonKey);
    }

    [Fact]
    public async Task ResolveAsync_ValidLinuxAction_ReturnsResolvedEscapedCommand()
    {
        var action = CommandLibraryTestHelpers.CreateLinuxAction(
            "tail-log",
            "Tail log",
            "tail -f {path}",
            CommandLibraryTestHelpers.RequiredParameter("path", "Path"));
        var provider = CommandLibraryTestHelpers.CreateResolverServiceProvider(action);
        var resolver = new CommandLibraryStepResolver(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await resolver.ResolveAsync(new PostConnectStep
        {
            CommandLibraryId = action.Id,
            CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["path"] = "/var/log/nginx access.log"
            }
        }, CancellationToken.None);

        Assert.Equal(PostConnectResolveStatus.Resolved, result.Status);
        Assert.Equal("tail -f '/var/log/nginx access.log'", result.ResolvedInput);
        Assert.Null(result.ReasonKey);
    }
}
