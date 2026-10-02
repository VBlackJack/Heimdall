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
using System.Xml.Linq;
using Heimdall.App.Services;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Guards for the fixes that came out of the RDP UI/UX audit. Behaviour is asserted on extracted
/// policies and on parsed markup; the few source reads carry a whole statement through
/// <see cref="ViewSource.IsStatementOfTheMethodBody"/>.
/// </summary>
public sealed class RdpUxAuditFixesTests
{
    private static readonly XNamespace s_xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("Error", false, true)]
    [InlineData("Disconnected", false, true)]
    [InlineData("Error", true, false)]
    [InlineData("Disconnected", true, false)]
    [InlineData("Connected", false, false)]
    [InlineData("Connecting", false, false)]
    [InlineData("Preparing", false, false)]
    [InlineData("Reconnecting", false, false)]
    [InlineData("Disconnecting", false, false)]
    public void ReconnectActionIsOfferedOnlyAfterAnEndingWithNoOverlay(
        string statusName,
        bool overlayVisible,
        bool expected)
    {
        var status = Enum.Parse<RdpSessionStatus>(statusName);

        Assert.Equal(expected, RdpConnectionPhasePolicy.IsReconnectActionVisible(status, overlayVisible));
    }

    [Fact]
    public void TheHeaderCarriesANamedReconnectButtonWiredToItsHandler()
    {
        var button = ViewSource.NamedElement("ReconnectButton");

        Assert.Equal("Button", ViewSource.TagName(button));
        Assert.False(string.IsNullOrWhiteSpace(ViewSource.AutomationAttribute(button, "Name")));
        Assert.Equal("Collapsed", (string?)button.Attribute("Visibility"));
        Assert.Equal("OnHeaderReconnectClick", (string?)button.Attribute("Click"));

        string handler = ViewSource.HandlerLogic("private void OnHeaderReconnectClick(");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(handler, "ReconnectRequested?.Invoke();"));
    }

    [Fact]
    public void TheStatusLineIsTrimmedAndCarriesTheFullTextInATooltip()
    {
        var status = ViewSource.NamedElement("StatusTextBlock");
        Assert.Equal("CharacterEllipsis", (string?)status.Attribute("TextTrimming"));
        Assert.NotNull(status.Attribute("MaxWidth"));

        string body = ViewSource.HandlerLogic("private void SetStatusText(");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(body, "StatusTextBlock.ToolTip = text;"));
    }

    [Fact]
    public void AFailureShowsTheLocalizedSentenceAndKeepsTheTechnicalDetailForTheTooltip()
    {
        string body = ViewSource.HandlerLogic("private void HandleFailure(");

        Assert.True(ViewSource.IsStatementOfTheMethodBody(body, "SetStatusTextWithDetail(message, detail);"));
    }

    [Fact]
    public void EscapeOnTheReconnectOverlayDoesNotDestroyThePane()
    {
        string body = ViewSource.HandlerLogic("private void OnReconnectOverlayPreviewKeyDown(");

        Assert.DoesNotContain("OnOverlayCloseClick", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseRequested", body, StringComparison.Ordinal);

        string dismiss = ViewSource.HandlerLogic("private void DismissReconnectOverlay(");
        Assert.DoesNotContain("CloseRequested", dismiss, StringComparison.Ordinal);
        Assert.True(ViewSource.IsStatementOfTheMethodBody(dismiss, "UpdateReconnectButtonVisibility();"));
    }

    [Theory]
    [InlineData("ReleaseFocus", true, true)]
    [InlineData("ReleaseFocus", false, false)]
    [InlineData("ToggleFullscreen", false, true)]
    [InlineData("ToggleFullscreen", true, true)]
    [InlineData("None", true, false)]
    [InlineData("None", false, false)]
    public void TheHookSwallowsAShortcutOnlyWhenItWillAct(string actionName, bool canReleaseFocus, bool expected)
    {
        var action = Enum.Parse<RdpKeyboardHookAction>(actionName);

        Assert.Equal(expected, RdpKeyboardHookShortcutRouter.ShouldConsume(action, canReleaseFocus));
    }

    [Fact]
    public void NoToastFallsBackToAnEmptyStringAndNoBrushFallsBackToALiteralColour()
    {
        string code = ViewSource.Code();

        Assert.DoesNotContain("] ?? string.Empty);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.White", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.IndianRed", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RedirectionIconsCarryATooltipAndTheLoadingBarHasAName()
    {
        string body = ViewSource.HandlerLogic("private void SetRedirectionIndicator(");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(body, "icon.ToolTip = helpText;"));

        var bar = ViewSource.NamedElement("RdpLoadingBar");
        Assert.False(string.IsNullOrWhiteSpace(ViewSource.AutomationAttribute(bar, "Name")));
    }

    [Theory]
    [InlineData("DlgSrv_RedirClipboardCb", "RdpRedirectClipboardHint")]
    [InlineData("DlgSrv_RedirDrivesCb", "RdpRedirectDrivesHint")]
    [InlineData("DlgSrv_RedirPrintersCb", "RdpRedirectPrintersHint")]
    [InlineData("DlgSrv_RedirComPortsCb", "RdpRedirectComPortsHint")]
    [InlineData("DlgSrv_RedirSmartCardsCb", "RdpRedirectSmartCardsHint")]
    [InlineData("DlgSrv_RedirUsbCb", "RdpRedirectUsbHint")]
    public void ProfileDialogRedirectionCheckBoxesCarryTheirHint(string checkBoxName, string hintKey)
    {
        XDocument dialog = XDocument.Load(Path.Combine(
            ViewSource.RepoRoot(), "src", "Heimdall.App", "Views", "Dialogs", "ServerDialog.xaml"));
        XElement box = dialog.Descendants()
            .Single(e => (string?)e.Attribute(s_xaml + "Name") == checkBoxName);
        string expected = "{loc:Translate " + hintKey + "}";

        Assert.Equal(expected, (string?)box.Attribute("ToolTip"));
        Assert.Equal(expected, (string?)box.Attribute("AutomationProperties.HelpText"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    [InlineData("es")]
    public async Task TheAuditedMessagesExistInEveryLanguageAndSayWhatToDoNext(string language)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);

        string[] deadEnds =
        [
            "RdpDisconnectNoInfo", "RdpDisconnectOutOfMemory", "RdpDisconnectUserNotFound",
            "RdpDisconnectAccountLockedOut", "RdpDisconnectAccountExpired", "RdpDisconnectUnknownCode",
        ];

        foreach (string key in deadEnds)
        {
            // The audit named these six as statements with no next step. Each now carries a
            // second sentence, so a single-sentence value is the regression.
            Assert.True(
                localizer[key].Split('.', StringSplitOptions.RemoveEmptyEntries).Length >= 2,
                $"{language}:{key} should state a next step in a second sentence");
        }

        foreach (string key in new[] { "RdpResolutionUpdateFailedToast", "A11yRdpConnectionProgress" })
        {
            Assert.NotEqual(key, localizer[key]);
        }
    }

    [Fact]
    public void TheViewNamesItsMagicNumbersAndStopsCountingMenuItems()
    {
        string code = ViewSource.Code();

        Assert.DoesNotContain("reason is 2308", code, StringComparison.Ordinal);
        Assert.DoesNotContain("reason is 0 or 1 or 2", code, StringComparison.Ordinal);
        Assert.DoesNotContain("const int StaticItemCount", code, StringComparison.Ordinal);
        Assert.DoesNotContain("const int totalSegments", code, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(1),", code, StringComparison.Ordinal);
        Assert.Equal("ResMenuPresetsSeparator", (string?)ViewSource.NamedElement("ResMenuPresetsSeparator")
            .Attribute(s_xaml + "Name"));
    }

    [Fact]
    public void ThePhaseStepperSegmentCountMatchesTheMarkupAndThePolicy()
    {
        int segments = ViewSource.Markup().Descendants()
            .Count(e => ((string?)e.Attribute(s_xaml + "Name"))?.StartsWith("PhaseSegment", StringComparison.Ordinal) == true);

        Assert.Equal(RdpConnectionPhasePolicy.SegmentCount, segments);
        Assert.Equal(
            RdpConnectionPhasePolicy.SegmentCount,
            RdpConnectionPhasePolicy.GetLitSegmentCount(RdpConnectionPhase.Connected));
    }

    [Theory]
    [InlineData("en", "Esc")]
    [InlineData("fr", "\u00c9chap")]
    [InlineData("es", "Esc")]
    public async Task ShortcutKeyNamesAreLocalized(string language, string escape)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), language);

        Assert.Equal(escape, localizer["RdpKeyNameEscape"]);
        Assert.NotEqual("RdpSessionDurationFormat", localizer["RdpSessionDurationFormat"]);
        Assert.NotEqual("RdpStabilizingTooltip", localizer["RdpStabilizingTooltip"]);
    }

    [Fact]
    public async Task TheFrenchDisconnectedMessageCarriesItsAccents()
    {
        LocalizationManager french = new();
        await french.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "fr");

        Assert.Equal("La session Bureau à distance s'est terminée.", french["RdpDisconnectedMessage"]);
    }
}
