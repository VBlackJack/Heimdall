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
/// Sleep prevention follows the setting live: turning it off releases the system, turning it
/// back on with sessions still open keeps the system awake again, and every continuous request
/// goes through the thread that owns it.
/// </summary>
[Collection(nameof(SleepPreventionTests))]
public sealed class SleepPreventionTests : IDisposable
{
    private const uint Hold = SleepPrevention.ES_CONTINUOUS | SleepPrevention.ES_SYSTEM_REQUIRED | SleepPrevention.ES_DISPLAY_REQUIRED;

    private readonly List<string> _requests = [];
    private readonly Action<uint> _originalSink;
    private readonly Action<Action> _originalThread;

    public SleepPreventionTests()
    {
        _originalSink = SleepPrevention.ExecutionStateSink;
        _originalThread = SleepPrevention.ContinuousStateThread;
        SleepPrevention.ResetForTests();
        SleepPrevention.ExecutionStateSink = flags =>
        {
            lock (_requests)
            {
                _requests.Add(flags == Hold ? "hold" : flags == SleepPrevention.ES_CONTINUOUS ? "release" : "heartbeat");
            }
        };
        SleepPrevention.ContinuousStateThread = action =>
        {
            lock (_requests)
            {
                _requests.Add("on-owner-thread");
            }

            action();
        };
    }

    public void Dispose()
    {
        SleepPrevention.ResetForTests();
        SleepPrevention.ExecutionStateSink = _originalSink;
        SleepPrevention.ContinuousStateThread = _originalThread;
    }

    [Fact]
    public void TurningTheSettingBackOn_WithASessionOpen_HoldsTheSystemAgain()
    {
        SleepPrevention.SessionStarted();
        SleepPrevention.Enabled = false;
        SleepPrevention.Enabled = true;

        Assert.True(SleepPrevention.IsHolding);
        Assert.Equal(1, SleepPrevention.ActiveSessionCount);
        Assert.Equal(["on-owner-thread", "hold", "on-owner-thread", "release", "on-owner-thread", "hold"], ContinuousRequests());
    }

    [Fact]
    public void ASessionStartedWhileOff_IsCountedAndHeldOnceTheSettingIsOn()
    {
        SleepPrevention.Enabled = false;
        SleepPrevention.SessionStarted();

        Assert.False(SleepPrevention.IsHolding);
        Assert.Empty(ContinuousRequests());

        SleepPrevention.Enabled = true;

        Assert.True(SleepPrevention.IsHolding);
    }

    [Fact]
    public void TheLastSessionEnding_ReleasesOnTheOwnerThread()
    {
        SleepPrevention.SessionStarted();
        SleepPrevention.SessionStarted();
        SleepPrevention.SessionEnded();

        Assert.True(SleepPrevention.IsHolding);

        SleepPrevention.SessionEnded();
        SleepPrevention.SessionEnded();

        Assert.False(SleepPrevention.IsHolding);
        Assert.Equal(0, SleepPrevention.ActiveSessionCount);
        Assert.Equal(["on-owner-thread", "hold", "on-owner-thread", "release"], ContinuousRequests());
    }

    [Fact]
    public void ForceRelease_ClearsTheCountAndTheHold()
    {
        SleepPrevention.SessionStarted();

        SleepPrevention.ForceRelease();

        Assert.False(SleepPrevention.IsHolding);
        Assert.Equal(0, SleepPrevention.ActiveSessionCount);
    }

    [Fact]
    public void IntervalSeconds_RejectsNonPositiveValues()
    {
        SleepPrevention.IntervalSeconds = 30;
        Assert.Equal(30, SleepPrevention.IntervalSeconds);

        SleepPrevention.IntervalSeconds = 0;
        Assert.Equal(60, SleepPrevention.IntervalSeconds);
    }

    private List<string> ContinuousRequests()
    {
        lock (_requests)
        {
            return _requests.Where(request => request != "heartbeat").ToList();
        }
    }
}
