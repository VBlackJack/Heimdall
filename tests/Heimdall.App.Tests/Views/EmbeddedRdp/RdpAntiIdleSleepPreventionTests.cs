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

using System.Reflection;
using Heimdall.App.Views;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Freezes that the RDP view does not write the thread execution state itself.
/// </summary>
/// <remarks>
/// <para>The anti-idle tick used to call <c>SetThreadExecutionState</c> with
/// <c>ES_CONTINUOUS | ES_DISPLAY_REQUIRED</c> on the UI thread. Two consequences, both silent. It
/// ran whether or not the user had allowed Heimdall to keep the machine awake, so unchecking
/// "prevent sleep during session" stopped meaning anything for a profile with anti-idle on. And
/// <c>SetThreadExecutionState</c> replaces the continuous flag set rather than merging into it, so
/// it withdrew the <c>ES_SYSTEM_REQUIRED</c> that the sleep-prevention service had set on that same
/// thread for every session the process was holding open.</para>
/// <para>Keeping the local display awake is that service's job and it already holds the display
/// request whenever the setting allows it. The assertion is on the compiled type rather than on the
/// source text: with no P/Invoke declared, the call cannot come back by accident.</para>
/// </remarks>
public sealed class RdpAntiIdleSleepPreventionTests
{
    [Fact]
    public void TheViewDeclaresNoExecutionStateEntryPoint()
    {
        Type nativeMethods = ResolveNativeMethods();

        string[] executionStateMembers = nativeMethods
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(member => member.Name)
            .Where(name => name.Contains("ExecutionState", StringComparison.Ordinal)
                || name.StartsWith("ES_", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            executionStateMembers.Length == 0,
            "The RDP view still owns the execution-state surface ("
                + string.Join(", ", executionStateMembers)
                + "). Sleep prevention is a single decision bound to a user setting; a second "
                + "writer on the same thread overrides the setting and drops flags the first one set.");
    }

    // Positive control: the type is really found and really is the one carrying the view's
    // P/Invokes, so a zero above is a measurement and not a missing lookup.
    [Fact]
    public void TheNativeMethodsTypeIsTheOneCarryingTheViewsOtherEntryPoints()
    {
        Type nativeMethods = ResolveNativeMethods();

        string[] members = nativeMethods
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(member => member.Name)
            .ToArray();

        Assert.Contains("PostMessage", members);
        Assert.Contains("FindWindowEx", members);
    }

    private static Type ResolveNativeMethods()
    {
        Type? nested = typeof(EmbeddedRdpView).GetNestedType(
            "NativeMethods",
            BindingFlags.Public | BindingFlags.NonPublic);

        Assert.True(nested is not null, "EmbeddedRdpView no longer declares a NativeMethods type.");
        return nested!;
    }
}
