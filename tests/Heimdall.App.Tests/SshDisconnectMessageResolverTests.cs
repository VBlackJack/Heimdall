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
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

/// <summary>
/// A-06 / D-08: the terminal's disconnect marker printed the SSH layer's English detail
/// in every locale. The resolver formats a keyed detail, localizes a classified failure
/// from its code, and relays anything else unchanged.
/// </summary>
public sealed class SshDisconnectMessageResolverTests
{
    private const string TargetLabel = "bastion.example.test";

    [Fact]
    public async Task Resolve_RemoteShellExited_FormatsTheFrenchCatalogueText()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("fr");
        SshSessionDisconnectInfo disconnect = SshShellSession.CreateShellEofDisconnectInfo(transportConnected: true);

        string? message = SshDisconnectMessageResolver.Resolve(disconnect, localizer, TargetLabel);

        Assert.Equal(localizer[SshDisconnectMessageKeys.MessageKeyRemoteShellExited], message);
        Assert.NotEqual(SshDisconnectMessageKeys.MessageKeyRemoteShellExited, message);
    }

    [Fact]
    public async Task Resolve_ProcessExit_FormatsTheExitCodeIntoTheFrenchTemplate()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("fr");
        SshSessionDisconnectInfo disconnect = TerminalReconnectPolicy.ClassifyProcessExit(
            exitCode: 137,
            autoReconnectOnProcessExit: false);

        string? message = SshDisconnectMessageResolver.Resolve(disconnect, localizer, TargetLabel);

        Assert.Equal(localizer.Format(SshLocalizationKeys.SshDisconnectProcessExited, 137), message);
        Assert.Contains("137", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ClassifiedFailure_LocalizesFromTheFailureCode()
    {
        LocalizationManager localizer = await LoadLocalizerAsync("fr");
        SshFailureInfo failure = new SshFailureInfo(SshFailureCode.NetworkReset, "Connection reset.", IsFatal: true);
        SshSessionDisconnectInfo disconnect = SshSessionDisconnectInfo.FromFailure(failure);

        string? message = SshDisconnectMessageResolver.Resolve(disconnect, localizer, TargetLabel);

        Assert.Equal(localizer.Format("ErrorSshNetworkReset", TargetLabel), message);
    }

    [Fact]
    public void Resolve_RelayedDetailWithoutLocalizer_ReturnsTheDetailUnchanged()
    {
        SshSessionDisconnectInfo disconnect = SshSessionDisconnectInfo.Unclassified("relayed by the server");

        string? message = SshDisconnectMessageResolver.Resolve(disconnect, null, TargetLabel);

        Assert.Equal("relayed by the server", message);
    }

    private static async Task<LocalizationManager> LoadLocalizerAsync(string language)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);
        return localizer;
    }
}
