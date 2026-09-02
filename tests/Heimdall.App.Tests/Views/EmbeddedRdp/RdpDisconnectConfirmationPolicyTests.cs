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

using Heimdall.App.Views.EmbeddedRdp;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that a disconnect confirmation which could not be answered is not a Yes.
/// </summary>
/// <remarks>
/// The view used to log the exception and fall straight through to the teardown, so the session
/// died with no prompt ever having been shown - the user reads a missing prompt as a UI glitch, not
/// as consent. The sibling confirmation for a resolution-driven reconnect already failed closed on
/// the same exception, so the two disagreed about the same question.
/// </remarks>
public sealed class RdpDisconnectConfirmationPolicyTests
{
    [Fact]
    public async Task AConfirmationThatThrowsDoesNotProceed()
    {
        Exception? reported = null;
        var boom = new InvalidOperationException("no owner window");

        bool proceed = await RdpDisconnectConfirmationPolicy.ConfirmAsync(
            () => throw boom,
            ex => reported = ex);

        Assert.False(proceed);
        Assert.Same(boom, reported);
    }

    [Fact]
    public async Task AYesProceedsAndANoDoesNot()
    {
        Assert.True(await RdpDisconnectConfirmationPolicy.ConfirmAsync(
            () => Task.FromResult(true),
            _ => Assert.Fail("no error was raised")));

        Assert.False(await RdpDisconnectConfirmationPolicy.ConfirmAsync(
            () => Task.FromResult(false),
            _ => Assert.Fail("no error was raised")));
    }

    [Fact]
    public void TheDisconnectHandlerRoutesThroughThePolicy()
    {
        string handler = ViewSource.HandlerBody("private async void OnDisconnectClick");

        Assert.Contains(
            "RdpDisconnectConfirmationPolicy.ConfirmAsync(",
            handler,
            StringComparison.Ordinal);
    }
}
