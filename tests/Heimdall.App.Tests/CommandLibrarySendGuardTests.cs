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

using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// Send runs nothing it should not: an invalid command, whose text is the raw pattern with its
/// braces, and a command with no terminal to receive it are both refused, by the method and by
/// the command the button and Ctrl+Enter go through.
/// </summary>
public sealed class CommandLibrarySendGuardTests
{
    [Fact]
    public async Task AnInvalidCommand_IsNotSent_EvenWithAHandlerAndATerminal()
    {
        using var viewModel = await CreateAsync(withRequiredParameter: true);
        var sent = new List<string>();
        viewModel.SendCommandHandler = sent.Add;
        viewModel.CanSendToTerminalProbe = () => true;

        Assert.False(viewModel.IsCommandValid);
        Assert.Contains("{pattern}", viewModel.GeneratedCommand, StringComparison.Ordinal);

        await viewModel.SendAsync();

        Assert.Empty(sent);
    }

    [Fact]
    public async Task AValidCommand_WithNoTerminal_IsNotSent()
    {
        using var viewModel = await CreateAsync(withRequiredParameter: false);
        var sent = new List<string>();
        viewModel.SendCommandHandler = sent.Add;
        viewModel.CanSendToTerminalProbe = () => false;

        await viewModel.SendAsync();

        Assert.Empty(sent);
    }

    [Fact]
    public async Task TheSendCommand_CannotExecuteAnInvalidCommand_AndCanAValidOne()
    {
        using var viewModel = await CreateAsync(withRequiredParameter: true);
        viewModel.SendCommandHandler = _ => { };
        viewModel.CanSendToTerminalProbe = () => true;

        Assert.False(viewModel.SendCommand.CanExecute(null));

        // Positive control: once the parameter has a value the same command can run, so the
        // refusal above is about validity and not a command that never runs.
        viewModel.Parameters.Single().Value = "error";

        Assert.True(viewModel.IsCommandValid);
        Assert.True(viewModel.SendCommand.CanExecute(null));
    }

    private static async Task<CommandLibraryViewModel> CreateAsync(bool withRequiredParameter)
    {
        var action = withRequiredParameter
            ? CommandLibraryTestHelpers.CreateLinuxAction(
                "grep", "Grep log", "grep {pattern} /var/log/syslog",
                CommandLibraryTestHelpers.RequiredParameter("pattern", "Pattern"))
            : CommandLibraryTestHelpers.CreateLinuxAction("uptime", "Uptime", "uptime");

        var viewModel = new CommandLibraryViewModel(
            CommandLibraryTestHelpers.CreateResolverServiceProvider(action),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            new SilentDialogService { ConfirmResult = true },
            gitSyncService: null!,
            transferService: null!);

        await viewModel.InitializeAsync(targetHost: null);
        viewModel.SelectAction(viewModel.AllEntries[0]);
        return viewModel;
    }
}
