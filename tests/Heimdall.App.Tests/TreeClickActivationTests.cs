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

using System.Windows.Input;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class TreeClickActivationTests
{
    private const string AlphaId = "alpha";
    private const string BetaId = "beta";
    private const int DoubleClickCount = 2;

    private static readonly TreeClickGesture PlainClick =
        new(ModifierKeys.None, ClickCount: 1, Dragged: false, PlainSelectionPress: true);

    // ---- The pure decision -----------------------------------------------------------------

    [Fact]
    public void AnOpenSession_IsBroughtToTheFront()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel other = CreateTab(BetaId);
        SessionTabViewModel tab = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [other, tab], active: other);

        Assert.Equal(TreeClickActivationKind.AttachedTab, result.Kind);
        Assert.Same(tab, result.Session);
    }

    [Fact]
    public void WithTheSettingOff_NothingHappens()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel tab = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [tab], active: null, enabled: false);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
        Assert.Null(result.Session);
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift)]
    public void AModifierKey_KeepsTheClickASelection(ModifierKeys modifiers)
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = Resolve(
            alpha,
            attached: [CreateTab(AlphaId)],
            active: null,
            gesture: PlainClick with { Modifiers = modifiers });

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void ADoubleClick_IsNotASingleClick()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = Resolve(
            alpha,
            attached: [CreateTab(AlphaId)],
            active: null,
            gesture: PlainClick with { ClickCount = DoubleClickCount });

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void ADrag_IsNotAClick()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = Resolve(
            alpha,
            attached: [CreateTab(AlphaId)],
            active: null,
            gesture: PlainClick with { Dragged = true });

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void ADeferredMultiSelectionPress_DoesNotActivate()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = Resolve(
            alpha,
            attached: [CreateTab(AlphaId)],
            active: null,
            gesture: PlainClick with { PlainSelectionPress = false });

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void AReleaseOnAnotherRow_DoesNotActivate()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        ServerItemViewModel beta = CreateServer(BetaId);

        TreeClickActivation result = TreeClickActivationDecision.Resolve(
            enabled: true,
            PlainClick,
            pressedTarget: alpha,
            releasedTarget: beta,
            attached: [CreateTab(AlphaId), CreateTab(BetaId)],
            active: null,
            detached: []);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void AReleaseOutsideAnyRow_DoesNotActivate()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = TreeClickActivationDecision.Resolve(
            enabled: true,
            PlainClick,
            pressedTarget: alpha,
            releasedTarget: null,
            attached: [CreateTab(AlphaId)],
            active: null,
            detached: []);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void AFolder_NeverActivates()
    {
        var folder = new FolderViewModel { Name = "Production", FullPath = "Production" };

        TreeClickActivation result = TreeClickActivationDecision.Resolve(
            enabled: true,
            PlainClick,
            pressedTarget: folder,
            releasedTarget: folder,
            attached: [CreateTab(AlphaId)],
            active: null,
            detached: []);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void AToolEntry_NeverActivates()
    {
        ServerItemViewModel tool = CreateServer(AlphaId, "TOOL:PING");

        TreeClickActivation result = Resolve(tool, attached: [CreateTab(AlphaId)], active: null);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void ASessionWithoutAnOpenTab_OnlyIsSelected()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [CreateTab(BetaId)], active: null);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void WithNoTabAtAll_OnlyTheSelectionHappens()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [], active: null);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void WhenTheActiveTabAlreadyShowsTheSession_NothingChanges()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel active = CreateTab(AlphaId);
        SessionTabViewModel later = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [active, later], active: active);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void WhenTheActiveTabShowsTheSessionInASecondaryPane_NothingChanges()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel split = CreateSplitTab(BetaId, AlphaId);
        SessionTabViewModel other = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [split, other], active: split);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    [Fact]
    public void ASessionInASecondaryPane_ActivatesTheHostTab()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel unrelated = CreateTab("gamma");
        SessionTabViewModel split = CreateSplitTab(BetaId, AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [unrelated, split], active: unrelated);

        Assert.Equal(TreeClickActivationKind.AttachedTab, result.Kind);
        Assert.Same(split, result.Session);
    }

    [Fact]
    public void SeveralMatches_PickTheFirstInStripOrder()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel unrelated = CreateTab(BetaId);
        SessionTabViewModel first = CreateTab(AlphaId);
        SessionTabViewModel second = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(alpha, attached: [unrelated, first, second], active: unrelated);

        Assert.Same(first, result.Session);
    }

    [Fact]
    public void ADetachedWindow_IsRaisedWhenNoAttachedTabMatches()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel detached = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(
            alpha,
            attached: [CreateTab(BetaId)],
            active: null,
            detached: [detached]);

        Assert.Equal(TreeClickActivationKind.DetachedWindow, result.Kind);
        Assert.Same(detached, result.Session);
    }

    [Fact]
    public void AnAttachedTab_WinsOverADetachedWindow()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        SessionTabViewModel attached = CreateTab(AlphaId);

        TreeClickActivation result = Resolve(
            alpha,
            attached: [attached],
            active: null,
            detached: [CreateTab(AlphaId)]);

        Assert.Equal(TreeClickActivationKind.AttachedTab, result.Kind);
        Assert.Same(attached, result.Session);
    }

    [Fact]
    public void ThePairedSessionId_IsTheInventoryIdNotTheRuntimeId()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        var pane = new SessionPaneModel { ServerId = "runtime-7f3a", OriginalServerId = AlphaId };
        var tab = new SessionTabViewModel { RootContent = pane };

        TreeClickActivation result = Resolve(alpha, attached: [tab], active: null);

        Assert.Same(tab, result.Session);
    }

    // ---- The press the release completes ---------------------------------------------------

    [Fact]
    public void ARecordedPress_IsHandedBackOnce()
    {
        var state = new TreeInteractionState();
        ServerItemViewModel alpha = CreateServer(AlphaId);

        state.RecordPointerPress(alpha, plainSelection: true, clickCount: 1);

        Assert.True(state.TryTakePointerPress(out TreePointerPress press));
        Assert.Same(alpha, press.Server);
        Assert.True(press.PlainSelection);
        Assert.Equal(1, press.ClickCount);
        Assert.False(state.TryTakePointerPress(out _));
    }

    [Fact]
    public void ANewGesture_DropsThePreviousPress()
    {
        var state = new TreeInteractionState();
        state.RecordPointerPress(CreateServer(AlphaId), plainSelection: true, clickCount: 1);

        state.ResetDrag();

        Assert.False(state.TryTakePointerPress(out _));
    }

    [Fact]
    public void ClearingThePress_DropsItBeforeALaterRelease()
    {
        var state = new TreeInteractionState();
        state.RecordPointerPress(CreateServer(AlphaId), plainSelection: true, clickCount: 1);

        state.ClearPointerPress();

        Assert.False(state.TryTakePointerPress(out _));
    }

    [Fact]
    public void ClearingWithoutAPress_IsHarmless()
    {
        var state = new TreeInteractionState();

        state.ClearPointerPress();

        Assert.False(state.TryTakePointerPress(out _));
    }

    [Fact]
    public void APaneWithoutAnInventoryId_NeverMatchesASession()
    {
        ServerItemViewModel alpha = CreateServer(AlphaId);
        var adHoc = new SessionTabViewModel
        {
            RootContent = new SessionPaneModel { ServerId = "adhoc-1", OriginalServerId = "" }
        };

        TreeClickActivation result = Resolve(alpha, attached: [adHoc], active: null);

        Assert.Equal(TreeClickActivationKind.None, result.Kind);
    }

    // ---- Detached windows ------------------------------------------------------------------

    [Fact]
    public void WithoutAFloatingWindow_RaisingASessionReportsFailure()
    {
        var service = new SessionWindowService();

        Assert.False(service.BringDetachedSessionToFront(CreateTab(AlphaId)));
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static TreeClickActivation Resolve(
        object target,
        IReadOnlyList<SessionTabViewModel> attached,
        SessionTabViewModel? active,
        IReadOnlyList<SessionTabViewModel>? detached = null,
        TreeClickGesture? gesture = null,
        bool enabled = true)
    {
        return TreeClickActivationDecision.Resolve(
            enabled,
            gesture ?? PlainClick,
            target,
            target,
            attached,
            active,
            detached ?? []);
    }

    private static ServerItemViewModel CreateServer(string id, string connectionType = "SSH")
    {
        return ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = id,
            DisplayName = id,
            RemoteServer = "server.example.test",
            ConnectionType = connectionType
        });
    }

    private static SessionTabViewModel CreateTab(string inventoryId)
    {
        var pane = new SessionPaneModel { ServerId = $"runtime-{Guid.NewGuid():N}", OriginalServerId = inventoryId };
        return new SessionTabViewModel { RootContent = pane };
    }

    private static SessionTabViewModel CreateSplitTab(string primaryInventoryId, string secondaryInventoryId)
    {
        var first = new SessionPaneModel { ServerId = $"runtime-{Guid.NewGuid():N}", OriginalServerId = primaryInventoryId };
        var second = new SessionPaneModel { ServerId = $"runtime-{Guid.NewGuid():N}", OriginalServerId = secondaryInventoryId };
        var root = new SplitContainerModel { First = first, Second = second, Orientation = SplitOrientation.Vertical };
        return new SessionTabViewModel { RootContent = root };
    }
}
