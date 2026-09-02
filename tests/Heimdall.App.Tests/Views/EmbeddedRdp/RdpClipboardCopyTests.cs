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

/// <summary>
/// Freezes that a clipboard write which did not land is reported as such.
/// </summary>
/// <remarks>
/// The clipboard is one shared Win32 resource and another process holding it makes the write throw.
/// The overlay's "Copy error" button used to swallow that: no toast, no other feedback, and the
/// user pastes whatever the clipboard already held into a support ticket.
/// </remarks>
public sealed class RdpClipboardCopyTests
{
    [Fact]
    public void AWriteThatThrowsIsReportedAsAFailure()
    {
        Exception? reported = null;
        var locked = new System.Runtime.InteropServices.ExternalException("CLIPBRD_E_CANT_OPEN");

        bool copied = RdpClipboardCopy.TryCopy(
            _ => throw locked,
            "payload",
            ex => reported = ex);

        Assert.False(copied);
        Assert.Same(locked, reported);
    }

    [Fact]
    public void AWriteThatLandsCarriesThePayload()
    {
        string? written = null;

        bool copied = RdpClipboardCopy.TryCopy(
            text => written = text,
            "line one\nline two",
            _ => Assert.Fail("no failure was raised"));

        Assert.True(copied);
        Assert.Equal("line one\nline two", written);
    }

    [Fact]
    public void TheCopyHandlerRoutesThroughTheHelperAndSaysWhichWayItWent()
    {
        string handler = ViewSource.HandlerBody("private void OnOverlayCopyErrorClick");

        Assert.Contains("RdpClipboardCopy.TryCopy(", handler, StringComparison.Ordinal);

        // Both outcomes are named at the call site, so a failed copy says so rather than saying
        // nothing at all.
        Assert.Contains("LocaleKeys.CopyErrorToast", handler, StringComparison.Ordinal);
        Assert.Contains("LocaleKeys.CopyErrorFailedToast", handler, StringComparison.Ordinal);
    }
}
