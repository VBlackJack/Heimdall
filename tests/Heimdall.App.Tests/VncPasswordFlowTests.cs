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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The password a VNC view sends, kept once the server accepted it and dropped once the server
/// refused it, from the answers only the view sees.
/// </summary>
public sealed class VncPasswordFlowTests
{
    private const string Typed = "typed-pw-2290";
    private const string Given = "given-pw-6613";

    // Kills: remembering when the user answers, before the server said anything.
    [Fact]
    public async Task ATypedPassword_WithRemember_IsKeptOnlyOnceTheSessionConnects()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, true));
        var flow = new VncPasswordFlow(null, keeper);

        Assert.True(await flow.AskAsync(CancellationToken.None));
        Assert.Equal(Typed, flow.Password);
        Assert.Empty(keeper.Remembered);

        await flow.OnConnectedAsync();

        Assert.Equal([Typed], keeper.Remembered);
    }

    // Kills: ignoring the Remember choice.
    [Fact]
    public async Task ATypedPassword_WithoutRemember_IsNeverKept()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, false));
        var flow = new VncPasswordFlow(null, keeper);

        await flow.AskAsync(CancellationToken.None);
        await flow.OnConnectedAsync();

        Assert.Empty(keeper.Remembered);
        Assert.Equal(Typed, flow.Password);
    }

    // A reconnect of the same session after a remembered one connected does not write it again.
    // Kills: a remember flag never cleared.
    [Fact]
    public async Task APasswordIsKeptOnce_NotOnEveryConnect()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, true));
        var flow = new VncPasswordFlow(null, keeper);

        await flow.AskAsync(CancellationToken.None);
        await flow.OnConnectedAsync();
        await flow.OnConnectedAsync();

        Assert.Single(keeper.Remembered);
    }

    // Kills: never forgetting, so a changed server password is sent forever.
    [Fact]
    public async Task ARefusalAfterThePassword_DropsTheKeptOne()
    {
        var keeper = new RecordingKeeper();
        var flow = new VncPasswordFlow(Given, keeper);

        await flow.OnSecurityFailureAsync(afterPassword: true);

        Assert.Equal(1, keeper.Forgotten);
        Assert.Null(flow.Password);
    }

    // A server refusing before any password (TigerVNC blocking the address after too many
    // failures, or no security type in common) says nothing of the password.
    // Kills: forgetting on every security failure.
    [Fact]
    public async Task ARefusalBeforeThePassword_KeepsIt()
    {
        var keeper = new RecordingKeeper();
        var flow = new VncPasswordFlow(Given, keeper);

        await flow.OnSecurityFailureAsync(afterPassword: false);

        Assert.Equal(0, keeper.Forgotten);
        Assert.Equal(Given, flow.Password);
    }

    // A typed password the server refused is not kept by a later connect. Kills: a refusal that
    // leaves the remember flag set.
    [Fact]
    public async Task ARefusedTypedPassword_IsNotKeptByALaterConnect()
    {
        var keeper = new RecordingKeeper(new PasswordPromptResult(Typed, true));
        var flow = new VncPasswordFlow(null, keeper);

        await flow.AskAsync(CancellationToken.None);
        await flow.OnSecurityFailureAsync(afterPassword: true);
        await flow.OnConnectedAsync();

        Assert.Empty(keeper.Remembered);
    }

    // Kills: a cancel read as an empty password, which would connect and be refused.
    [Fact]
    public async Task ACancelledQuestion_ChangesNothing()
    {
        var keeper = new RecordingKeeper(answer: null);
        var flow = new VncPasswordFlow(Given, keeper);

        Assert.False(await flow.AskAsync(CancellationToken.None));
        await flow.OnConnectedAsync();

        Assert.Equal(Given, flow.Password);
        Assert.Empty(keeper.Remembered);
        Assert.Equal(0, keeper.Forgotten);
    }

    // A profile of the user's own holds its password: nothing to ask, keep or drop.
    [Fact]
    public async Task WithoutAKeeper_ItOnlyCarriesThePassword()
    {
        var flow = new VncPasswordFlow(Given, keeper: null);

        Assert.False(flow.CanAsk);
        Assert.False(await flow.AskAsync(CancellationToken.None));
        await flow.OnSecurityFailureAsync(afterPassword: true);
        Assert.Null(flow.Password);
        Assert.Null(new VncPasswordFlow(string.Empty, null).Password);
    }

    private sealed class RecordingKeeper(PasswordPromptResult? answer = null) : IVncPasswordKeeper
    {
        public List<string> Remembered { get; } = [];

        public int Forgotten { get; private set; }

        public Task<PasswordPromptResult?> AskAsync(CancellationToken cancellationToken) => Task.FromResult(answer);

        public Task RememberAsync(string password)
        {
            Remembered.Add(password);
            return Task.CompletedTask;
        }

        public Task ForgetAsync()
        {
            Forgotten++;
            return Task.CompletedTask;
        }
    }
}
