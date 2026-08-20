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

public sealed class RdpDisconnectActionPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(260)]
    [InlineData(516)]
    [InlineData(1030)]
    [InlineData(2055)]
    [InlineData(2056)]
    [InlineData(2308)]
    [InlineData(2311)]
    [InlineData(2825)]
    [InlineData(3080)]
    [InlineData(3848)]
    [InlineData(4360)]
    [InlineData(9999)]
    public void ShouldOfferEditProfile_AlwaysReturnsTrue(int? disconnectCode)
    {
        var actual = RdpDisconnectActionPolicy.ShouldOfferEditProfile(disconnectCode);

        Assert.True(actual);
    }

    [Theory]
    [InlineData(2055)]
    [InlineData(2308)]
    [InlineData(2311)]
    [InlineData(2825)]
    [InlineData(3080)]
    [InlineData(3848)]
    public void ResolvePrimaryAction_ReturnsEditProfile_ForProfileRemediationCodes(int disconnectCode)
    {
        var actual = RdpDisconnectActionPolicy.ResolvePrimaryAction(disconnectCode);

        Assert.Equal(RdpOverlayPrimaryAction.EditProfile, actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(2054)]
    [InlineData(2056)]
    [InlineData(4359)]
    [InlineData(4361)]
    [InlineData(99999)]
    // 3592 and 4360 both mean the client failed to reconnect to the session. 4360 sat in the
    // profile-remediation list on the strength of a meaning that has since been refuted, so it
    // pre-focused an action that could not fix anything. This row is the correction of that
    // pinning, not its removal.
    [InlineData(3592)]
    [InlineData(4360)]
    public void ResolvePrimaryAction_ReturnsReconnect_ForOtherCodes(int disconnectCode)
    {
        var actual = RdpDisconnectActionPolicy.ResolvePrimaryAction(disconnectCode);

        Assert.Equal(RdpOverlayPrimaryAction.Reconnect, actual);
    }

    [Fact]
    public void ResolvePrimaryAction_ReturnsReconnect_ForNullCode()
    {
        var actual = RdpDisconnectActionPolicy.ResolvePrimaryAction(null);

        Assert.Equal(RdpOverlayPrimaryAction.Reconnect, actual);
    }

    [Theory]
    [InlineData(2055)]
    [InlineData(2308)]
    [InlineData(2311)]
    [InlineData(2825)]
    [InlineData(3080)]
    [InlineData(3848)]
    public void ResolvePrimaryAction_EditProfileCodes_AreAlsoOfferedAsEditProfileActions(int disconnectCode)
    {
        var actual = RdpDisconnectActionPolicy.ResolvePrimaryAction(disconnectCode);

        Assert.Equal(RdpOverlayPrimaryAction.EditProfile, actual);
        Assert.True(RdpDisconnectActionPolicy.ShouldOfferEditProfile(disconnectCode));
    }

    /// <summary>
    /// Two codes that say the same thing to the user must offer the same first action.
    /// </summary>
    /// <remarks>
    /// <para>The policy is indexed by code and the meaning lives in the message key, so nothing
    /// structural connected them: 3592 and 4360 resolve to one message and used to resolve to two
    /// different primary actions, and the suite was green either way because both lists were
    /// written out by hand and agreed with themselves.</para>
    /// <para>The pairs are derived by sweeping the decoder rather than listed here, so a future
    /// code added to an existing message arm is covered the day it is added.</para>
    /// </remarks>
    [Fact]
    public void CodesSharingAMessage_ShareAPrimaryAction()
    {
        Dictionary<string, List<int>> byMessage = [];
        for (int code = 0; code <= ushort.MaxValue; code++)
        {
            string? key = Heimdall.Rdp.ActiveX.RdpActiveXHost.GetDisconnectReasonKey(code);
            if (key is null)
            {
                continue;
            }

            if (!byMessage.TryGetValue(key, out List<int>? codes))
            {
                codes = [];
                byMessage[key] = codes;
            }

            codes.Add(code);
        }

        List<string> disagreements = [];
        int sharedMessages = 0;
        foreach ((string key, List<int> codes) in byMessage)
        {
            if (codes.Count < 2)
            {
                continue;
            }

            sharedMessages++;
            List<RdpOverlayPrimaryAction> actions =
                [.. codes.Select(static sharedCode => RdpDisconnectActionPolicy.ResolvePrimaryAction(sharedCode))];
            if (actions.Distinct().Count() > 1)
            {
                disagreements.Add(
                    $"'{key}' is shown for codes {string.Join(", ", codes)} but their primary "
                        + $"actions are {string.Join(", ", actions)}");
            }
        }

        // Guarding the guard: with no shared message anywhere the loop above asserts nothing, and
        // it would still report success.
        Assert.True(sharedMessages > 0, "no message is shared by two codes, so nothing was compared");
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }
}
