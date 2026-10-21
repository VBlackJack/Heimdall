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
using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using CommandTemplate = TwinShell.Core.Models.CommandTemplate;

namespace Heimdall.App.Services.PostConnect;

public sealed class CommandLibraryStepResolver(IServiceScopeFactory scopeFactory) : IPostConnectStepResolver
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// Post-connect steps run only over SSH terminals, which is the connection type the platform
    /// is resolved against when the profile declares none.
    /// </summary>
    private const string PostConnectConnectionType = "SSH";

    public Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, CancellationToken ct)
        => ResolveAsync(step, RemoteOs.Auto, ct);

    public async Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, RemoteOs remoteOs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(step);

        if (string.IsNullOrWhiteSpace(step.CommandLibraryId))
        {
            return new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.Literal,
                ResolvedInput = step.Input
            };
        }

        ct.ThrowIfCancellationRequested();

        using var scope = _scopeFactory.CreateScope();
        var actionService = scope.ServiceProvider.GetRequiredService<IActionService>();
        var generator = scope.ServiceProvider.GetRequiredService<ICommandGeneratorService>();

        var action = await actionService.GetActionByIdAsync(step.CommandLibraryId).ConfigureAwait(false);
        if (action is null)
        {
            FileLogger.Info($"Post-connect resolve: action '{step.CommandLibraryId}' missing.");
            return new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.BrokenMissing,
                ReasonKey = "LogPostConnectResolveBrokenMissing"
            };
        }

        var template = SelectTemplate(action, remoteOs);
        if (template is null)
        {
            FileLogger.Info($"Post-connect resolve: action '{action.Id}' has no usable template.");
            return new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.BrokenNoTemplate,
                ReasonKey = "LogPostConnectResolveBrokenNoTemplate"
            };
        }

        try
        {
            var values = step.CommandLibraryParams ?? [];
            var command = generator.GenerateCommand(template, values);
            FileLogger.Info($"Post-connect resolve: action '{action.Id}' resolved.");
            return new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.Resolved,
                ResolvedInput = command
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentNullException)
        {
            FileLogger.Info($"Post-connect resolve: action '{action.Id}' invalid parameters.");
            return new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.BrokenInvalidParams,
                ReasonKey = "LogPostConnectResolveBrokenInvalidParams"
            };
        }
    }

    /// <summary>
    /// The template a linked action expands to on a far end that runs <paramref name="remoteOs"/>.
    /// </summary>
    /// <remarks>
    /// A profile that declares Windows - SSH into a Windows OpenSSH server - gets the Windows
    /// template first. Otherwise post-connect runs over SSH, where a POSIX shell is the dominant
    /// case, so the Linux template is preferred. Either way the other template is the fallback, so
    /// a linked action that carries only one pattern still resolves instead of silently breaking.
    /// </remarks>
    internal static CommandTemplate? SelectTemplate(TwinShell.Core.Models.Action action, RemoteOs remoteOs)
    {
        ArgumentNullException.ThrowIfNull(action);

        return RemoteOsResolver.Resolve(remoteOs, PostConnectConnectionType) == RemoteOs.Windows
            ? action.WindowsCommandTemplate ?? action.LinuxCommandTemplate
            : action.LinuxCommandTemplate ?? action.WindowsCommandTemplate;
    }
}
