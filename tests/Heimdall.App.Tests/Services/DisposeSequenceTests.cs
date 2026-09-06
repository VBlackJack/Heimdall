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

namespace Heimdall.App.Tests.Services;

/// <summary>The teardown runs whatever the releases ahead of it do.</summary>
public sealed class DisposeSequenceTests
{
    [Fact]
    public void Run_PrologueSucceeds_RunsTeardownAndReportsNothing()
    {
        List<string> calls = [];

        DisposeSequence.Run(
            () => calls.Add("prologue"),
            () => calls.Add("teardown"),
            _ => calls.Add("failure"));

        Assert.Equal(["prologue", "teardown"], calls);
    }

    [Fact]
    public void Run_PrologueThrows_ReportsTheFailureAndStillRunsTeardown()
    {
        List<string> calls = [];
        Exception? reported = null;

        DisposeSequence.Run(
            () => throw new NullReferenceException("late unsubscribe"),
            () => calls.Add("teardown"),
            ex => reported = ex);

        Assert.Equal(["teardown"], calls);
        Assert.IsType<NullReferenceException>(reported);
    }

    [Fact]
    public void Run_ReportingThrows_StillRunsTeardown()
    {
        List<string> calls = [];

        Assert.Throws<InvalidOperationException>(() => DisposeSequence.Run(
            () => throw new NullReferenceException(),
            () => calls.Add("teardown"),
            _ => throw new InvalidOperationException("logger gone")));

        Assert.Equal(["teardown"], calls);
    }
}
