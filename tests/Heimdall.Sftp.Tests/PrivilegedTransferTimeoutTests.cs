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

namespace Heimdall.Sftp.Tests;

/// <summary>
/// The bound a privileged transfer's exec command carries.
/// </summary>
/// <remarks>
/// SSH.NET leaves <c>SshCommand.CommandTimeout</c> infinite, and its bound is wall-clock over the
/// whole command, not an inactivity timer. A fixed bound therefore trades two failures: too short
/// aborts a large legitimate write, too long (or infinite) leaves a wedged server holding the
/// editor's background save forever. The write's bound grows with its payload so that neither
/// happens; a command whose payload is not known carries the control bound.
/// </remarks>
public sealed class PrivilegedTransferTimeoutTests
{
    private const long OneGibibyte = 1024L * 1024 * 1024;

    /// <summary>Below this a bound aborts legitimate privileged work instead of catching a wedge.</summary>
    private static readonly TimeSpan ShortestUsefulControlBound = TimeSpan.FromMinutes(1);

    /// <summary>Above this the bound is finite in name only for a command with no payload.</summary>
    private static readonly TimeSpan LongestUsefulControlBound = TimeSpan.FromMinutes(30);

    /// <summary>A link slower than this is not one a privileged transfer is expected to finish on.</summary>
    private const long SlowestSupportedBytesPerSecond = 8 * 1024;

    /// <summary>A rate floor above this would abort ordinary transfers over a modest WAN link.</summary>
    private const long FastestAcceptableRateFloor = 64 * 1024;

    [Fact]
    public void TheControlBoundSitsBetweenBothFailureModes()
    {
        TimeSpan bound = PrivilegedFileTransfer.ControlCommandTimeout;

        Assert.NotEqual(Timeout.InfiniteTimeSpan, bound);
        Assert.InRange(bound, ShortestUsefulControlBound, LongestUsefulControlBound);
    }

    [Fact]
    public void AnUnknownOrEmptyPayloadCarriesTheControlBound()
    {
        Assert.Equal(PrivilegedFileTransfer.ControlCommandTimeout, PrivilegedFileTransfer.TransferCommandTimeout(null));
        Assert.Equal(PrivilegedFileTransfer.ControlCommandTimeout, PrivilegedFileTransfer.TransferCommandTimeout(0));
    }

    // Both sides, because each is a decision: the floor rate must be low enough that a large write
    // over a slow link is not aborted, and high enough that a wedge is still caught within the day.
    [Fact]
    public void TheWriteBoundGrowsWithThePayload_AtARateFloorThatIsADecisionOnBothSides()
    {
        long rate = PrivilegedFileTransfer.MinimumTransferBytesPerSecond;
        Assert.InRange(rate, SlowestSupportedBytesPerSecond, FastestAcceptableRateFloor);

        TimeSpan bound = PrivilegedFileTransfer.TransferCommandTimeout(OneGibibyte);

        Assert.Equal(
            PrivilegedFileTransfer.ControlCommandTimeout + TimeSpan.FromSeconds((double)OneGibibyte / rate),
            bound);
        Assert.True(
            PrivilegedFileTransfer.TransferCommandTimeout(2 * OneGibibyte) > bound,
            "a larger payload must never get a shorter bound");
    }

    [Fact]
    public void ANegativePayloadIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PrivilegedFileTransfer.TransferCommandTimeout(-1));
    }
}
