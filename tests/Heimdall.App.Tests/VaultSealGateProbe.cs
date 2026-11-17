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

using Heimdall.Core.Security.Vault;

namespace Heimdall.App.Tests;

/// <summary>
/// Proves that a writer takes the shared side of the process-wide seal gate: while the exclusive
/// side is held the write must not finish, and it must land once the side is released.
/// </summary>
/// <remarks>
/// The exclusive side is always released in a <c>finally</c>, and every wait is bounded, so a
/// failing assertion cannot leave the gate held for the other classes of the collection.
/// </remarks>
internal static class VaultSealGateProbe
{
    private static readonly TimeSpan HoldTime = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    /// <summary>Runs <paramref name="write"/> while the exclusive side is held and checks that it waited.</summary>
    public static async Task AssertWaitsForTheExclusiveSideAsync(Func<Task> write, Func<Task<bool>>? hasLanded = null)
    {
        IDisposable exclusive = await VaultSealGate.Shared.EnterExclusiveAsync(Bound);
        Task running;
        bool finishedWhileHeld;
        bool landedWhileHeld = false;
        try
        {
            running = Task.Run(write);
            finishedWhileHeld = await Task.WhenAny(running, Task.Delay(HoldTime)) == running;
            if (hasLanded is not null)
            {
                landedWhileHeld = await hasLanded();
            }
        }
        finally
        {
            exclusive.Dispose();
        }

        await running.WaitAsync(Bound);
        Assert.False(finishedWhileHeld, "the write has to wait for the exclusive side of the seal gate");
        Assert.False(landedWhileHeld, "the write must not land while the exclusive side is held");
        if (hasLanded is not null)
        {
            Assert.True(await hasLanded(), "the write lands once the exclusive side is released");
        }
    }
}
