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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Tests;

public sealed class RdpDisconnectCodeFormattingTests
{
    [Theory]
    [InlineData(2055, "RDP_BAD_CREDENTIALS | 2055")]
    [InlineData(264, "RDP_CONNECTION_TIMEOUT | 264")]
    [InlineData(260, "RDP_DNS_LOOKUP_FAILED | 260")]
    [InlineData(3848, "RDP_CRED_SSP_POLICY_ERROR | 3848")]
    [InlineData(9999, "RDP_UNKNOWN | 9999")]
    [InlineData(0, "RDP_NO_INFO | 0")]
    public void FormatDisconnectCode_ReturnsSymbolicNameAndRawCode(int reason, string expected)
    {
        var actual = RdpActiveXHost.FormatDisconnectCode(reason);

        Assert.Equal(expected, actual);
    }

    // The code is pasted into tickets, mails and consoles from the clipboard report. Every
    // character must survive a Windows console code page and a re-encoding ticket system, so
    // the whole string is pinned to ASCII rather than to one particular separator.
    [Theory]
    [InlineData(2055, RdpActiveXHost.NoExtendedDisconnectReason)]
    [InlineData(2308, 0x0300_0032)]
    [InlineData(9999, 4096)]
    public void FormatDisconnectCode_IsAscii(int reason, int extendedReason)
    {
        var actual = RdpActiveXHost.FormatDisconnectCode(reason, extendedReason);

        Assert.All(actual, c => Assert.True(char.IsAscii(c), $"non-ASCII U+{(int)c:X4} in '{actual}'"));
    }

    [Fact]
    public void DisconnectCodeSeparator_IsAscii()
    {
        Assert.All(
            RdpActiveXHost.DisconnectCodeSeparator,
            c => Assert.True(char.IsAscii(c), $"non-ASCII U+{(int)c:X4} in the separator"));
    }
}
