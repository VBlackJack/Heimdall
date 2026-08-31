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
using System.Threading;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class SingleInstanceGuardTests
{
    /// <summary>
    /// A fresh data root per test. Named kernel objects outlive a test method, so a
    /// shared name would let one test decide another's outcome, and the pair would
    /// still pass in isolation.
    /// </summary>
    private static string UniqueDataRoot() =>
        Path.Combine(Path.GetTempPath(), "HeimdallGuard", Guid.NewGuid().ToString("N"));

    [Fact]
    public void TryAcquire_FirstCaller_BecomesOwner()
    {
        string root = UniqueDataRoot();

        var outcome = SingleInstanceGuard.TryAcquire(root, () => { }, out var guard);

        using (guard)
        {
            Assert.Equal(SingleInstanceOutcome.Owner, outcome);
            Assert.NotNull(guard);
        }
    }

    /// <summary>
    /// The behaviour the guard exists for, and the one that cannot be assumed: a named
    /// mutex requested with <c>initiallyOwned</c> must report that it already existed
    /// rather than wait for the owner to let go. The timeout below is the assertion -
    /// a blocking constructor would hang this test rather than fail it.
    /// </summary>
    [Fact]
    public void TryAcquire_SecondCaller_IsTurnedAwayWithoutBlocking()
    {
        string root = UniqueDataRoot();

        var first = SingleInstanceGuard.TryAcquire(root, () => { }, out var owner);
        using (owner)
        {
            Assert.Equal(SingleInstanceOutcome.Owner, first);

            SingleInstanceOutcome second = SingleInstanceOutcome.Owner;
            var probe = new Thread(() =>
                second = SingleInstanceGuard.TryAcquire(root, () => { }, out _));
            probe.IsBackground = true;
            probe.Start();

            Assert.True(
                probe.Join(TimeSpan.FromSeconds(5)),
                "The second acquisition blocked. A named mutex created with initiallyOwned"
                + " must return ownership only when it created the object, never wait for it.");

            Assert.Equal(SingleInstanceOutcome.AlreadyRunning, second);
        }
    }

    [Fact]
    public void TryAcquire_SecondCaller_AsksTheOwnerToComeForward()
    {
        string root = UniqueDataRoot();
        using var surfaced = new ManualResetEventSlim(false);

        var first = SingleInstanceGuard.TryAcquire(root, () => surfaced.Set(), out var owner);
        using (owner)
        {
            Assert.Equal(SingleInstanceOutcome.Owner, first);
            Assert.False(surfaced.IsSet, "Nothing has asked the owner to surface yet.");

            SingleInstanceGuard.TryAcquire(root, () => { }, out _);

            Assert.True(
                surfaced.Wait(TimeSpan.FromSeconds(5)),
                "The second launch shut down without asking the running instance to surface,"
                + " which loses the user's click instead of honouring it.");
        }
    }

    /// <summary>
    /// Releasing must let the next launch through. Without this, a normal exit would
    /// lock the user out of their own application until they signed out.
    /// </summary>
    [Fact]
    public void Dispose_ReleasesOwnership_ForTheNextLaunch()
    {
        string root = UniqueDataRoot();

        var first = SingleInstanceGuard.TryAcquire(root, () => { }, out var owner);
        Assert.Equal(SingleInstanceOutcome.Owner, first);
        owner!.Dispose();

        var second = SingleInstanceGuard.TryAcquire(root, () => { }, out var next);
        using (next)
        {
            Assert.Equal(SingleInstanceOutcome.Owner, second);
        }
    }


    /// <summary>
    /// The opt-out exists for the UI test host, which builds the real application inside
    /// the test process and would otherwise own the developer's data root. Tests in one
    /// class run sequentially, so setting a process-wide variable here cannot race the
    /// acquisition tests above.
    /// </summary>
    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("0 ", false)]
    public void IsDisabledByEnvironment_OnlyAnExplicitZeroDisablesTheGuard(
        string value,
        bool expected)
    {
        string? previous = Environment.GetEnvironmentVariable(
            SingleInstanceGuard.DisableEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                SingleInstanceGuard.DisableEnvironmentVariable, value);

            Assert.Equal(expected, SingleInstanceGuard.IsDisabledByEnvironment());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SingleInstanceGuard.DisableEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void TryAcquire_WhenDisabled_StartsUnguardedRatherThanTakingOwnership()
    {
        string root = UniqueDataRoot();
        string? previous = Environment.GetEnvironmentVariable(
            SingleInstanceGuard.DisableEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                SingleInstanceGuard.DisableEnvironmentVariable, "0");

            var outcome = SingleInstanceGuard.TryAcquire(root, () => { }, out var guard);

            // Unavailable, never AlreadyRunning: an opt-out must let the process run, not
            // convince it that somebody else is already there.
            Assert.Equal(SingleInstanceOutcome.Unavailable, outcome);
            Assert.Null(guard);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SingleInstanceGuard.DisableEnvironmentVariable, previous);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\Heimdall", @"C:\Users\x\AppData\Local\Heimdall\")]
    [InlineData(@"C:\Users\x\AppData\Local\Heimdall", @"c:\users\x\appdata\local\heimdall")]
    public void BuildNames_TreatsEquivalentPathsAsTheSameDirectory(string left, string right)
    {
        Assert.Equal(SingleInstanceGuard.BuildNames(left), SingleInstanceGuard.BuildNames(right));
    }

    /// <summary>
    /// The companion the equivalence cases cannot provide: a derivation that returned a
    /// constant would satisfy every case above and exclude every user from every other.
    /// </summary>
    [Fact]
    public void BuildNames_SeparatesDistinctDirectories()
    {
        var left = SingleInstanceGuard.BuildNames(@"C:\Users\alice\AppData\Local\Heimdall");
        var right = SingleInstanceGuard.BuildNames(@"C:\Users\bob\AppData\Local\Heimdall");

        Assert.NotEqual(left, right);
        Assert.NotEqual(left.MutexName, right.MutexName);
    }

    /// <summary>
    /// The two objects must not share a name: one is a mutex and the other an event, and
    /// a collision would make the second creation fail with a type mismatch at startup.
    /// </summary>
    [Fact]
    public void BuildNames_UsesDistinctNamesForTheMutexAndTheEvent()
    {
        var names = SingleInstanceGuard.BuildNames(@"C:\Users\x\AppData\Local\Heimdall");

        Assert.NotEqual(names.MutexName, names.ActivationEventName);
        Assert.StartsWith(@"Local\", names.MutexName, StringComparison.Ordinal);
        Assert.StartsWith(@"Local\", names.ActivationEventName, StringComparison.Ordinal);
    }
}
