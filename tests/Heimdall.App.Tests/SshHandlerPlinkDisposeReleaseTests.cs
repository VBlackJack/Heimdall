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
using Heimdall.App.Services;
using Heimdall.App.Services.Handlers;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Pageant;
using Xunit;

namespace Heimdall.App.Tests;

/// <summary>
/// Closing a tab disposes the session, which kills the process and detaches its exit
/// notification: the password file and the temporary size session must go then.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SshHandlerPlinkDisposeReleaseTests
{
    private const string TypedPassword = "typed-secret";

    private static List<ISshAgent> NoAgents() =>
        [new SshHandlerAgentKeyTests.FakeAgent(PageantAgent.AgentName, [])];

    [Fact]
    public async Task SessionDisposed_DeletesThePasswordFile()
    {
        using var harness = await SshHandlerPlinkAgentOnlyTests.PlinkPromptHarness.CreateAsync(
            NoAgents(), TypedPassword);

        ConnectionResult result = await harness.ConnectViaPlinkAsync();
        Assert.True(result.Success, result.ErrorMessage);
        string passwordFile = Assert.Single(harness.PasswordFilePaths);
        Assert.True(File.Exists(passwordFile));

        ((TerminalSessionResult)result.Session!).Session.Dispose();

        Assert.False(File.Exists(passwordFile));
    }

    [Fact]
    public void ReleaseArmedAsPrepareLaunchArmsIt_DisposedThenSecondTrigger_DeletesExactlyOnce()
    {
        // The handler arms the handle on the session and also subscribes it to Disposed. The exit
        // and the first-output paths both call Release, so a direct call is the second trigger; a
        // pipe session cannot raise its exit from outside.
        List<string?> deletions = [];
        using Heimdall.Terminal.PipeModeSession session = new();
        PlinkPasswordFileReleaseHandle handle = PlinkPasswordFileRelease.Arm(
            session, "password-file", deletions.Add, firstByteProvesConsumption: false);
        session.Disposed += handle.Release;

        session.Dispose();
        handle.Release();

        Assert.Equal(["password-file"], deletions);
    }

    [Fact]
    public void ReleaseArmedAsPrepareLaunchArmsIt_FirstTriggerThenDisposed_DeletesExactlyOnce()
    {
        List<string?> deletions = [];
        using Heimdall.Terminal.PipeModeSession session = new();
        PlinkPasswordFileReleaseHandle handle = PlinkPasswordFileRelease.Arm(
            session, "password-file", deletions.Add, firstByteProvesConsumption: false);
        session.Disposed += handle.Release;

        handle.Release();
        session.Dispose();

        Assert.Equal(["password-file"], deletions);
    }
}
