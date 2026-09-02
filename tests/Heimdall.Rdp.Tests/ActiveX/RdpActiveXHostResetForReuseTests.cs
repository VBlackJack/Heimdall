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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// What the reset does with a credential clear that did not succeed.
/// </summary>
/// <remarks>
/// The clear is the one step of the reset whose failure cannot be recovered later: the view has
/// already unsubscribed from Disconnected and the COM sink is already detached by the time the
/// pool asks for the reset, so nothing else runs afterwards. Reporting success anyway pushes a
/// control that still holds the previous session's plaintext password onto the idle stack, and
/// the log line is the same either way.
/// </remarks>
public sealed class RdpActiveXHostResetForReuseTests
{
    [Fact]
    public void AFailedCredentialClear_VetoesReuse()
    {
        var stateWasReset = false;

        bool reusable = RdpActiveXHost.TryCompleteResetForReuse(
            clearRemoteCredential: () => false,
            resetSessionState: () => stateWasReset = true);

        Assert.False(reusable);
        Assert.False(stateWasReset);
    }

    /// <summary>
    /// The positive control: reuse is what the pool exists for, and a clear that succeeded must
    /// not cost it.
    /// </summary>
    [Fact]
    public void AClearedCredential_AllowsReuseAndResetsTheRest()
    {
        var stateWasReset = false;

        bool reusable = RdpActiveXHost.TryCompleteResetForReuse(
            clearRemoteCredential: () => true,
            resetSessionState: () => stateWasReset = true);

        Assert.True(reusable);
        Assert.True(stateWasReset);
    }
}
