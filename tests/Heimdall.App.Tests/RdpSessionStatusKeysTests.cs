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

using System.IO;
using System.Text.Json;
using Heimdall.App.Services;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Rdp.Display;

namespace Heimdall.App.Tests;

public sealed class RdpSessionStatusKeysTests
{
    [Theory]
    [InlineData((int)RdpSessionStatus.Preparing, "RdpStatusPreparing")]
    [InlineData((int)RdpSessionStatus.Connecting, "RdpStatusConnecting")]
    [InlineData((int)RdpSessionStatus.Connected, "RdpStatusConnected")]
    [InlineData((int)RdpSessionStatus.Disconnecting, "RdpStatusDisconnecting")]
    [InlineData((int)RdpSessionStatus.Disconnected, "RdpStatusDisconnected")]
    [InlineData((int)RdpSessionStatus.Reconnecting, "RdpStatusReconnecting")]
    [InlineData((int)RdpSessionStatus.Error, "RdpStatusError")]
    public void GetKey_ReturnsStableKey(int statusValue, string expected)
    {
        var status = (RdpSessionStatus)statusValue;

        Assert.Equal(expected, RdpSessionStatusKeys.GetKey(status));
    }

    [Fact]
    public void EveryEnumValue_HasAnInvariantCode()
    {
        foreach (var status in Enum.GetValues<RdpSessionStatus>())
        {
            var code = RdpSessionStatusKeys.GetInvariantCode(status);

            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.DoesNotContain(' ', code);
        }
    }

    [Fact]
    public void EveryEnumValue_ResolvesToLocaleKeyPresentInEnglish()
    {
        using var document = LoadLocaleDocument("en");

        foreach (var status in Enum.GetValues<RdpSessionStatus>())
        {
            var key = RdpSessionStatusKeys.GetKey(status);

            Assert.True(
                document.RootElement.TryGetProperty(key, out _),
                $"Locale key '{key}' for {status} is missing from en.json");
        }
    }

    /// <summary>
    /// Every reason the product can coerce a session for has to have something to say about it.
    /// </summary>
    /// <remarks>
    /// Driven from the enumeration rather than from a list of keys, so a reason added later without
    /// a message fails here instead of coercing a session in silence. The parity check in CI
    /// compares the two languages against each other; it cannot see a key the code asks for and
    /// neither language has.
    /// </remarks>
    [Fact]
    public void EveryMultimonFallbackReason_HasAMessageInBothLanguages()
    {
        using var english = LoadLocaleDocument("en");
        using var french = LoadLocaleDocument("fr");
        List<string> missing = [];
        int checkedReasons = 0;

        foreach (MultimonFallbackReason reason in Enum.GetValues<MultimonFallbackReason>())
        {
            if (reason == MultimonFallbackReason.None)
            {
                continue;
            }

            checkedReasons++;
            string? key = EmbeddedSessionManager.ResolveMultimonFallbackStatusKey(reason);
            if (key is null)
            {
                missing.Add($"{reason}: no status key");
                continue;
            }

            if (!english.RootElement.TryGetProperty(key, out _))
            {
                missing.Add($"{reason}: '{key}' missing from en.json");
            }

            if (!french.RootElement.TryGetProperty(key, out _))
            {
                missing.Add($"{reason}: '{key}' missing from fr.json");
            }
        }

        Assert.Empty(missing);

        // A reason set that shrank to nothing would otherwise read as a pass.
        Assert.Equal(Enum.GetValues<MultimonFallbackReason>().Length - 1, checkedReasons);
        Assert.True(checkedReasons >= 3);
    }

    [Theory]
    [InlineData("RdpMultimonFallbackSingleMonitor")]
    [InlineData("RdpMultimonFallbackInvalidSelection")]
    [InlineData("RdpMultimonFallbackNonContiguous")]
    public void MultimonFallbackKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("RdpMultimonFallbackSingleMonitor")]
    [InlineData("RdpMultimonFallbackInvalidSelection")]
    [InlineData("RdpMultimonFallbackNonContiguous")]
    public void MultimonFallbackKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("BtnCancelReconnect")]
    [InlineData("TooltipCancelReconnect")]
    [InlineData("A11yCancelReconnect")]
    public void CancelReconnectKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("BtnCancelReconnect")]
    [InlineData("TooltipCancelReconnect")]
    [InlineData("A11yCancelReconnect")]
    public void CancelReconnectKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("RdpAutofillSearching")]
    [InlineData("RdpAutofillFilled")]
    [InlineData("RdpAutofillTimedOut")]
    [InlineData("RdpAutofillFailed")]
    public void AutofillKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("RdpAutofillSearching")]
    [InlineData("RdpAutofillFilled")]
    [InlineData("RdpAutofillTimedOut")]
    [InlineData("RdpAutofillFailed")]
    public void AutofillKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("RdpAspectPreserve")]
    [InlineData("RdpAspect16x9")]
    [InlineData("RdpAspect4x3")]
    [InlineData("RdpAspect21x9")]
    public void AspectRatioKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("RdpAspectPreserve")]
    [InlineData("RdpAspect16x9")]
    [InlineData("RdpAspect4x3")]
    [InlineData("RdpAspect21x9")]
    public void AspectRatioKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Fact]
    public void ExternalClientStatusKey_IsPresentInEnglish()
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty("StatusLaunchedExternalClient", out _),
            "Locale key 'StatusLaunchedExternalClient' is missing from en.json");
    }

    [Fact]
    public void ExternalClientStatusKey_IsPresentInFrench()
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty("StatusLaunchedExternalClient", out _),
            "Locale key 'StatusLaunchedExternalClient' is missing from fr.json");
    }

    [Theory]
    [InlineData("SplitForcedEmbeddedTitle")]
    [InlineData("SplitForcedEmbeddedMessage")]
    public void SplitForcedEmbeddedKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("SplitForcedEmbeddedTitle")]
    [InlineData("SplitForcedEmbeddedMessage")]
    public void SplitForcedEmbeddedKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("TooltipSendCtrlAltDel")]
    [InlineData("A11ySendCtrlAltDel")]
    public void SendCtrlAltDelKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("TooltipSendCtrlAltDel")]
    [InlineData("A11ySendCtrlAltDel")]
    public void SendCtrlAltDelKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("TooltipAntiIdleActive")]
    [InlineData("A11yAntiIdleActive")]
    public void AntiIdleIndicatorKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("TooltipAntiIdleActive")]
    [InlineData("A11yAntiIdleActive")]
    public void AntiIdleIndicatorKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Fact]
    public void AntiIdleHint_MentionsShiftInEnglish()
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(document.RootElement.TryGetProperty("RdpAntiIdleHint", out var value));
        Assert.Contains("Shift", value.GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AntiIdleHint_MentionsMajInFrench()
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(document.RootElement.TryGetProperty("RdpAntiIdleHint", out var value));
        Assert.Contains("Maj", value.GetString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CitrixLaunchCommandRejected")]
    [InlineData("CitrixModeCacheLaunch")]
    [InlineData("CitrixModeIcaFile")]
    [InlineData("CitrixModeStoreFront")]
    [InlineData("CitrixCaptureSearching")]
    [InlineData("CitrixCaptureSearchingExtended")]
    [InlineData("CitrixCancelCapture")]
    [InlineData("CitrixConfirmTerminateTitle")]
    [InlineData("CitrixConfirmTerminateMessage")]
    [InlineData("CitrixUseSsoTooltip")]
    public void CitrixBatchKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("CitrixLaunchCommandRejected")]
    [InlineData("CitrixModeCacheLaunch")]
    [InlineData("CitrixModeIcaFile")]
    [InlineData("CitrixModeStoreFront")]
    [InlineData("CitrixCaptureSearching")]
    [InlineData("CitrixCaptureSearchingExtended")]
    [InlineData("CitrixCancelCapture")]
    [InlineData("CitrixConfirmTerminateTitle")]
    [InlineData("CitrixConfirmTerminateMessage")]
    [InlineData("CitrixUseSsoTooltip")]
    public void CitrixBatchKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("RdpRedirectWebcamHint")]
    [InlineData("RdpAdminMode")]
    [InlineData("RdpAdminModeHint")]
    [InlineData("RdpFullScreen")]
    [InlineData("RdpFullScreenHint")]
    public void ModeAwareRdpKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("RdpRedirectWebcamHint")]
    [InlineData("RdpAdminMode")]
    [InlineData("RdpAdminModeHint")]
    [InlineData("RdpFullScreen")]
    [InlineData("RdpFullScreenHint")]
    public void ModeAwareRdpKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("ServerDialogRdpTestButton")]
    [InlineData("ServerDialogRdpTestCancel")]
    [InlineData("ServerDialogRdpTestSuccess")]
    [InlineData("ServerDialogRdpTestInvalidAddress")]
    [InlineData("ServerDialogRdpTestInvalidPort")]
    [InlineData("ServerDialogRdpTestDnsTimeout")]
    [InlineData("ServerDialogRdpTestDnsFailed")]
    [InlineData("ServerDialogRdpTestDnsNoResults")]
    [InlineData("ServerDialogRdpTestTcpTimeout")]
    [InlineData("ServerDialogRdpTestTcpFailed")]
    [InlineData("ServerDialogRdpTestCancelled")]
    public void RdpTestConnectionKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("ServerDialogRdpTestButton")]
    [InlineData("ServerDialogRdpTestCancel")]
    [InlineData("ServerDialogRdpTestSuccess")]
    [InlineData("ServerDialogRdpTestInvalidAddress")]
    [InlineData("ServerDialogRdpTestInvalidPort")]
    [InlineData("ServerDialogRdpTestDnsTimeout")]
    [InlineData("ServerDialogRdpTestDnsFailed")]
    [InlineData("ServerDialogRdpTestDnsNoResults")]
    [InlineData("ServerDialogRdpTestTcpTimeout")]
    [InlineData("ServerDialogRdpTestTcpFailed")]
    [InlineData("ServerDialogRdpTestCancelled")]
    public void RdpTestConnectionKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("ServerDialogRdpUsernameWatermark")]
    [InlineData("ServerDialogRdpUsernameHint")]
    [InlineData("ServerDialogRdpUsingGlobalDefaultsBanner")]
    public void RdpServerDialogProfileUxKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("ServerDialogRdpUsernameWatermark")]
    [InlineData("ServerDialogRdpUsernameHint")]
    [InlineData("ServerDialogRdpUsingGlobalDefaultsBanner")]
    public void RdpServerDialogProfileUxKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("SettingsRdpAdvancedTimeoutsHeader")]
    [InlineData("SettingsLabelRdpResizeEnableDelay")]
    [InlineData("SettingsLabelRdpArtifactCleanupDelay")]
    [InlineData("SettingsLabelRdpCredentialAutofillTimeout")]
    public void AdvancedTimeoutKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("SettingsRdpAdvancedTimeoutsHeader")]
    [InlineData("SettingsLabelRdpResizeEnableDelay")]
    [InlineData("SettingsLabelRdpArtifactCleanupDelay")]
    [InlineData("SettingsLabelRdpCredentialAutofillTimeout")]
    public void AdvancedTimeoutKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("RdpSurfaceNotReady")]
    public void SurfaceNotReadyKey_IsPresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("RdpSurfaceNotReady")]
    public void SurfaceNotReadyKey_IsPresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Theory]
    [InlineData("RdpErrorDecryptPassword")]
    [InlineData("RdpErrorStoreCredentials")]
    [InlineData("RdpErrorRdpFileWrite")]
    [InlineData("RdpErrorMstscLaunch")]
    [InlineData("RdpErrorLaunchFailed")]
    [InlineData("A11yRdpPhaseAnnouncementFormat")]
    [InlineData("A11yRdpReconnectDialog")]
    [InlineData("RdpConfirmDisconnectTitle")]
    [InlineData("RdpConfirmDisconnectMessage")]
    [InlineData("RdpDisconnectMessagePrefixFormat")]
    [InlineData("RdpDisconnectSeverityPrefixNotice")]
    [InlineData("RdpDisconnectSeverityPrefixWarning")]
    [InlineData("RdpDisconnectSeverityPrefixError")]
    [InlineData("RdpCopyErrorHeader")]
    [InlineData("RdpCopyErrorTimeLabel")]
    [InlineData("RdpCopyErrorServerLabel")]
    [InlineData("RdpCopyErrorTunnelLabel")]
    [InlineData("RdpCopyErrorSessionLabel")]
    [InlineData("RdpCopyErrorAppLabel")]
    [InlineData("RdpCopyErrorTunnelValueFormat")]
    [InlineData("RdpCopyErrorSessionDurationFormat")]
    [InlineData("RdpCopyErrorToast")]
    [InlineData("RdpBitmapCachingHint")]
    [InlineData("RdpAutoReconnectHint")]
    [InlineData("RdpDisableUdpHint")]
    [InlineData("A11yRdpTransientToast")]
    [InlineData("RdpFullscreenExitHint")]
    [InlineData("RdpRedirectionStatusOnFormat")]
    [InlineData("RdpRedirectionStatusOffFormat")]
    [InlineData("RdpResolutionSkipStabilization")]
    [InlineData("RdpMultimonFallbackSingleMonitor")]
    [InlineData("RdpMultimonFallbackInvalidSelection")]
    [InlineData("RdpSendKeysSentToast")]
    [InlineData("RdpSendKeysSentFailedToast")]
    [InlineData("RdpShortcutsHelpDisconnect")]
    [InlineData("RdpShortcutsHelpFullscreen")]
    [InlineData("RdpShortcutsHelpMenuItem")]
    [InlineData("RdpShortcutsHelpReleaseFocus")]
    [InlineData("RdpShortcutsHelpResolution")]
    [InlineData("RdpShortcutsHelpSendKeysEntry")]
    [InlineData("RdpShortcutsHelpSendKeysSection")]
    [InlineData("RdpShortcutsHelpSplit")]
    [InlineData("RdpShortcutsHelpTitle")]
    [InlineData("RdpShortcutsHelpToolbarSection")]
    [InlineData("RdpStabilizationSkippedToast")]
    [InlineData("RdpStatusInitializingSurface")]
    [InlineData("SessionSaveAsProfile")]
    public void RdpHandlerUserFacingErrorKeys_ArePresentInEnglish(string key)
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from en.json");
    }

    [Theory]
    [InlineData("RdpErrorDecryptPassword")]
    [InlineData("RdpErrorStoreCredentials")]
    [InlineData("RdpErrorRdpFileWrite")]
    [InlineData("RdpErrorMstscLaunch")]
    [InlineData("RdpErrorLaunchFailed")]
    [InlineData("A11yRdpPhaseAnnouncementFormat")]
    [InlineData("A11yRdpReconnectDialog")]
    [InlineData("RdpConfirmDisconnectTitle")]
    [InlineData("RdpConfirmDisconnectMessage")]
    [InlineData("RdpDisconnectMessagePrefixFormat")]
    [InlineData("RdpDisconnectSeverityPrefixNotice")]
    [InlineData("RdpDisconnectSeverityPrefixWarning")]
    [InlineData("RdpDisconnectSeverityPrefixError")]
    [InlineData("RdpCopyErrorHeader")]
    [InlineData("RdpCopyErrorTimeLabel")]
    [InlineData("RdpCopyErrorServerLabel")]
    [InlineData("RdpCopyErrorTunnelLabel")]
    [InlineData("RdpCopyErrorSessionLabel")]
    [InlineData("RdpCopyErrorAppLabel")]
    [InlineData("RdpCopyErrorTunnelValueFormat")]
    [InlineData("RdpCopyErrorSessionDurationFormat")]
    [InlineData("RdpCopyErrorToast")]
    [InlineData("RdpBitmapCachingHint")]
    [InlineData("RdpAutoReconnectHint")]
    [InlineData("RdpDisableUdpHint")]
    [InlineData("A11yRdpTransientToast")]
    [InlineData("RdpFullscreenExitHint")]
    [InlineData("RdpRedirectionStatusOnFormat")]
    [InlineData("RdpRedirectionStatusOffFormat")]
    [InlineData("RdpResolutionSkipStabilization")]
    [InlineData("RdpMultimonFallbackSingleMonitor")]
    [InlineData("RdpMultimonFallbackInvalidSelection")]
    [InlineData("RdpSendKeysSentToast")]
    [InlineData("RdpSendKeysSentFailedToast")]
    [InlineData("RdpShortcutsHelpDisconnect")]
    [InlineData("RdpShortcutsHelpFullscreen")]
    [InlineData("RdpShortcutsHelpMenuItem")]
    [InlineData("RdpShortcutsHelpReleaseFocus")]
    [InlineData("RdpShortcutsHelpResolution")]
    [InlineData("RdpShortcutsHelpSendKeysEntry")]
    [InlineData("RdpShortcutsHelpSendKeysSection")]
    [InlineData("RdpShortcutsHelpSplit")]
    [InlineData("RdpShortcutsHelpTitle")]
    [InlineData("RdpShortcutsHelpToolbarSection")]
    [InlineData("RdpStabilizationSkippedToast")]
    [InlineData("RdpStatusInitializingSurface")]
    [InlineData("SessionSaveAsProfile")]
    public void RdpHandlerUserFacingErrorKeys_ArePresentInFrench(string key)
    {
        using var document = LoadLocaleDocument("fr");

        Assert.True(
            document.RootElement.TryGetProperty(key, out _),
            $"Locale key '{key}' is missing from fr.json");
    }

    [Fact]
    public void RdpStatusReconnecting_HasAttemptAndCapPlaceholdersInEnglish()
    {
        using var document = LoadLocaleDocument("en");

        Assert.True(document.RootElement.TryGetProperty("RdpStatusReconnecting", out var value));
        var reconnecting = value.GetString() ?? string.Empty;

        Assert.Contains("{0}", reconnecting);
        Assert.Contains("{1}", reconnecting);
    }

    private static JsonDocument LoadLocaleDocument(string locale)
    {
        var localesPath = FindLocalesPath();
        var jsonPath = Path.Combine(localesPath, $"{locale}.json");

        Assert.True(File.Exists(jsonPath), $"{locale}.json not found at {jsonPath}");

        return JsonDocument.Parse(File.ReadAllText(jsonPath));
    }

    private static string FindLocalesPath()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "locales");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "en.json")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "locales"));

        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new DirectoryNotFoundException(
            "Cannot find locales/ directory. Ensure the test runs from the repository.");
    }
}
