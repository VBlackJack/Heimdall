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

public sealed class RdpHostFocusPolicyTests
{
    private static readonly IntPtr Host = new(100);
    private static readonly IntPtr Inner = new(200);
    private static readonly IntPtr Other = new(300);

    private static bool Descends(IntPtr parent, IntPtr child) => parent == Host && child == Inner;

    [Fact]
    public void FocusIsTheHostItself_IsInside()
        => Assert.True(RdpHostFocusPolicy.IsFocusInsideHost(Host, Host, (_, _) => false));

    [Fact]
    public void FocusIsADescendantOfTheHost_IsInside()
        => Assert.True(RdpHostFocusPolicy.IsFocusInsideHost(Inner, Host, Descends));

    [Fact]
    public void FocusIsAnotherWindow_IsOutside()
        => Assert.False(RdpHostFocusPolicy.IsFocusInsideHost(Other, Host, Descends));

    [Fact]
    public void NoFocus_IsOutside_AndDoesNotAskTheDelegate()
        => Assert.False(RdpHostFocusPolicy.IsFocusInsideHost(IntPtr.Zero, Host, (_, _) => throw new InvalidOperationException()));

    [Fact]
    public void HostHandleNotCreated_IsOutside_AndDoesNotAskTheDelegate()
        => Assert.False(RdpHostFocusPolicy.IsFocusInsideHost(Inner, IntPtr.Zero, (_, _) => throw new InvalidOperationException()));
}
