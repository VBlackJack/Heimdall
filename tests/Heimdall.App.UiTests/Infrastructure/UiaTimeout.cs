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
/// Turns a UI Automation call that timed out into "not yet" for one probe of a bounded wait.
/// </summary>
/// <remarks>
/// A product that is busy (a WebView2 page starting, a shell attaching) can leave UI Automation
/// without an answer for longer than its connection timeout, and the call then throws
/// <see cref="COMException"/> 0x80131505. Inside a poll, that is a probe that learned nothing:
/// the wait goes on and its own deadline still bounds it. Every other failure is rethrown.
/// <para>
/// A probe that times out never reads as an answer: <see cref="MissOnTimeout{T}"/> returns
/// <see langword="null"/> (not found yet) and <see cref="FalseOnTimeout"/> returns
/// <see langword="false"/> (condition not met yet), so a wait for something to disappear is not
/// satisfied by a search that could not look.
/// </para>
/// </remarks>
public static class UiaTimeout
{
    // COR_E_TIMEOUT, what UI Automation reports when the provider does not answer in time.
    public const int TimeoutHResult = unchecked((int)0x80131505);

    /// <summary>Whether <paramref name="exception"/> is a UI Automation timeout.</summary>
    public static bool IsTimeout(Exception exception) =>
        exception is COMException com && com.HResult == TimeoutHResult;

    /// <summary>Runs <paramref name="probe"/>; a UI Automation timeout reads as not found.</summary>
    public static T? MissOnTimeout<T>(Func<T?> probe)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            return probe();
        }
        catch (COMException ex) when (IsTimeout(ex))
        {
            return null;
        }
    }

    /// <summary>Runs <paramref name="predicate"/>; a UI Automation timeout reads as not met.</summary>
    public static bool FalseOnTimeout(Func<bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        try
        {
            return predicate();
        }
        catch (COMException ex) when (IsTimeout(ex))
        {
            return false;
        }
    }
}
