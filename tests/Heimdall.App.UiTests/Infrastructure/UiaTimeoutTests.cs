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

using System.Runtime.InteropServices;

namespace Heimdall.App.UiTests.Infrastructure;

/// <summary>
/// A UI Automation timeout inside a poll is "not yet", never an answer, and nothing else is
/// swallowed with it.
/// </summary>
public sealed class UiaTimeoutTests
{
    // E_FAIL: a COM failure that is not a timeout.
    private const int OtherHResult = unchecked((int)0x80004005);
    private const string Found = "found";

    [Fact]
    public void MissOnTimeout_ATimeout_ReadsAsNotFound()
    {
        Assert.Null(UiaTimeout.MissOnTimeout<string>(() => throw new COMException(null, UiaTimeout.TimeoutHResult)));
    }

    [Fact]
    public void MissOnTimeout_AnAnswer_IsReturned()
    {
        Assert.Equal(Found, UiaTimeout.MissOnTimeout(() => Found));
    }

    [Fact]
    public void MissOnTimeout_AnotherComFailure_IsRethrown()
    {
        var thrown = Assert.Throws<COMException>(
            () => UiaTimeout.MissOnTimeout<string>(() => throw new COMException(null, OtherHResult)));
        Assert.Equal(OtherHResult, thrown.HResult);
    }

    [Fact]
    public void FalseOnTimeout_ATimeout_ReadsAsNotMet()
    {
        // A wait for a window to close must not be satisfied by a search that could not look.
        Assert.False(UiaTimeout.FalseOnTimeout(() => throw new COMException(null, UiaTimeout.TimeoutHResult)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FalseOnTimeout_AnAnswer_IsReturned(bool answer)
    {
        Assert.Equal(answer, UiaTimeout.FalseOnTimeout(() => answer));
    }

    [Fact]
    public void FalseOnTimeout_AnotherComFailure_IsRethrown()
    {
        Assert.Throws<COMException>(
            () => UiaTimeout.FalseOnTimeout(() => throw new COMException(null, OtherHResult)));
    }
}
