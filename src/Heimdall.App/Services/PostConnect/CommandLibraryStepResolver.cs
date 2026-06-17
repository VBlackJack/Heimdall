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

using Heimdall.Core.Logging;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;

namespace Heimdall.App.Services.PostConnect;

public sealed class CommandLibraryStepResolver(IServiceScopeFactory scopeFactory) : IPostConnectStepResolver
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    public async Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, CancellationToken ct)
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

        // Post-connect runs only over SSH terminals, where a POSIX shell is the
        // dominant case, so the Linux template is preferred. Fall back to the
        // Windows template (e.g. SSH into a Windows OpenSSH host) so a linked
        // action that only carries a Windows pattern still resolves instead of
        // silently breaking.
        var template = action.LinuxCommandTemplate ?? action.WindowsCommandTemplate;
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
}
