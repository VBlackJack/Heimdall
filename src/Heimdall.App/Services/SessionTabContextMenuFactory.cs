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
using System.Windows;
using System.Windows.Controls;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// Builds the WPF <see cref="ContextMenu"/> for a session tab header
/// right-click. Composition branches on the session type (tool vs
/// connection), split state and hosted view type (an
/// <see cref="EmbeddedSshView"/> unlocks the transcript and macro items).
/// Extracted from <c>MainWindow.xaml.cs</c> to reduce code-behind size
/// and enable targeted unit testing of the menu-building logic.
/// </summary>
/// <remarks>
/// Window-layer actions that need access to <see cref="MainWindow"/>
/// state (fullscreen, floating window creation, split orchestration) are
/// routed through <see cref="ISessionTabContextCallbacks"/>. Everything
/// else (command dispatch, dialog prompts, status-text updates,
/// localization, settings access) is invoked directly on
/// <see cref="MainViewModel"/>.
/// </remarks>
public sealed class SessionTabContextMenuFactory
{
    /// <summary>Locale key of the "Active mode" caption that opens the resolution header.</summary>
    private const string ActiveModeLabelKey = "RdpResolutionActiveModeLabel";

    /// <summary>
    /// Initialises a new <see cref="SessionTabContextMenuFactory"/>.
    /// </summary>
    public SessionTabContextMenuFactory()
    {
    }

    /// <summary>
    /// Builds the right-click context menu for a session tab. The caller
    /// is responsible for opening the returned menu (setting
    /// <see cref="ContextMenu.PlacementTarget"/>, <see cref="ContextMenu.Placement"/>
    /// and <see cref="ContextMenu.IsOpen"/>).
    /// </summary>
    /// <param name="session">The tab the menu is for.</param>
    /// <param name="vm">The main view model.</param>
    /// <param name="callbacks">Window-layer actions.</param>
    /// <param name="clickedPane">The pane of the tab the user right-clicked, when the click landed in one.</param>
    public ContextMenu CreateMenu(
        SessionTabViewModel session,
        MainViewModel vm,
        ISessionTabContextCallbacks callbacks,
        SessionPaneModel? clickedPane = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(callbacks);

        var isToolTab = ConnectionTypeCatalog.IsToolConnectionType(session.ConnectionType);

        // Sections from the most frequent to the most destructive: what the session does, how it
        // is shown, the tab itself, its profile, the terminal, the split, and closing last, away
        // from where the pointer lands. The close entry used to be the very first one.
        IReadOnlyList<object>[] sections =
        [
            Section(host =>
            {
                if (!isToolTab)
                {
                    AppendSessionActions(host, session, vm, callbacks);
                }
            }),
            Section(host =>
            {
                if (!isToolTab && session.PrimaryPane.HostControl is EmbeddedRdpView rdpView)
                {
                    AppendResolutionMenu(host, session, vm, callbacks, rdpView);
                }
            }),
            Section(host =>
            {
                AppendTitleAndPinItems(host, session, vm);
                AppendDetachItem(host, session, vm, callbacks);
            }),
            Section(host =>
            {
                if (!isToolTab)
                {
                    AppendProfileActions(host, session, vm, callbacks);
                }
            }),
            Section(host =>
            {
                if (session.HostControl is EmbeddedSshView sshView)
                {
                    AppendTranscriptItem(host, session, vm, sshView);
                    AppendMacroItems(host, vm, sshView);
                }

                AppendMaskedCopyItem(host, session, vm, clickedPane);
            }),
            Section(host => AppendSplitItems(host, session, vm, callbacks)),
            Section(host =>
            {
                AppendCloseGroupItems(host, session, vm);
                AppendCloseItem(host, session, vm);
            }),
        ];

        var menu = new ContextMenu();
        foreach (object item in JoinSections(sections, static () => new Separator()))
        {
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>
    /// Lays sections end to end with one separator between two non-empty sections, and none at
    /// either end. Pure; a section that has nothing for this tab leaves no trace.
    /// </summary>
    /// <remarks>
    /// The sections used to carry their own separators, so a section that came out empty - the
    /// resolution menu outside RDP - left two separators touching.
    /// </remarks>
    internal static IReadOnlyList<object> JoinSections(
        IEnumerable<IReadOnlyList<object>> sections,
        Func<object> createSeparator)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(createSeparator);

        var items = new List<object>();
        foreach (IReadOnlyList<object> section in sections)
        {
            if (section.Count == 0)
            {
                continue;
            }

            if (items.Count > 0)
            {
                items.Add(createSeparator());
            }

            items.AddRange(section);
        }

        return items;
    }

    /// <summary>
    /// Runs a section builder against a scratch menu and hands its items back, detached so they
    /// can join the real menu.
    /// </summary>
    private static IReadOnlyList<object> Section(Action<ContextMenu> fill)
    {
        var host = new ContextMenu();
        fill(host);
        List<object> items = host.Items.Cast<object>().ToList();
        host.Items.Clear();
        return items;
    }

    /// <summary>
    /// Every session except <paramref name="current"/> and any pinned tab,
    /// preserving the order of <paramref name="ordered"/>. Pure (no side effects);
    /// used by "Close others".
    /// </summary>
    internal static IReadOnlyList<SessionTabViewModel> SessionsToCloseOthers(
        IReadOnlyList<SessionTabViewModel> ordered,
        SessionTabViewModel current)
    {
        return ordered
            .Where(session => !ReferenceEquals(session, current) && !session.IsPinned && !session.IsTabGroupCollapsed)
            .ToList();
    }

    /// <summary>
    /// Every session positioned after <paramref name="current"/> in
    /// <paramref name="ordered"/> (tab order) that is not pinned. Empty when
    /// <paramref name="current"/> is the last tab or is not present. Pure; used by
    /// "Close to the right".
    /// </summary>
    internal static IReadOnlyList<SessionTabViewModel> SessionsToCloseToRight(
        IReadOnlyList<SessionTabViewModel> ordered,
        SessionTabViewModel current)
    {
        var index = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ReferenceEquals(ordered[i], current))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return [];
        }

        var result = new List<SessionTabViewModel>(ordered.Count - index - 1);
        for (var i = index + 1; i < ordered.Count; i++)
        {
            // A tab folded in a collapsed group is out of sight: closing it from a gesture the
            // user aimed at the visible strip took tabs they had only put away.
            if (!ordered[i].IsPinned && !ordered[i].IsTabGroupCollapsed)
            {
                result.Add(ordered[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Every tab of the folder <paramref name="folderKey"/>, in tab order, the tab the menu was
    /// opened on included. Pure; used by "Close this folder's tabs".
    /// </summary>
    internal static IReadOnlyList<SessionTabViewModel> SessionsInFolder(
        IReadOnlyList<SessionTabViewModel> ordered,
        string folderKey)
        => ordered
            .Where(session => string.Equals(session.TabGroupKey, folderKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

    // --- Rename / Reset title / Pin ---

    private static void AppendTitleAndPinItems(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm)
    {
        var renameItem = new MenuItem { Header = vm.Localize("SessionRenameTab") };
        renameItem.Click += async (_, _) =>
        {
            var entered = await vm.DialogService.ShowInputAsync(
                vm.Localize("SessionRenameTitle"),
                vm.Localize("SessionRenamePrompt"),
                session.DisplayTitle);

            // Null = cancelled: leave the title unchanged. A cleared (blank) value
            // resets to the auto title.
            if (entered is not null)
            {
                var trimmed = entered.Trim();
                session.CustomTitle = string.IsNullOrEmpty(trimmed) ? null : trimmed;
            }
        };
        menu.Items.Add(renameItem);

        if (!string.IsNullOrWhiteSpace(session.CustomTitle))
        {
            var resetItem = new MenuItem { Header = vm.Localize("SessionResetTitle") };
            resetItem.Click += (_, _) => session.CustomTitle = null;
            menu.Items.Add(resetItem);
        }

        var pinItem = new MenuItem
        {
            Header = vm.Localize(session.IsPinned ? "SessionUnpinTab" : "SessionPinTab")
        };
        pinItem.Click += (_, _) => vm.Connection.SetPinned(session, !session.IsPinned);
        menu.Items.Add(pinItem);

        AppendTabGroupItems(menu, session, vm);
    }

    // --- Folder groups ---

    private static void AppendTabGroupItems(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm)
    {
        var groupItem = new MenuItem
        {
            Header = vm.Localize("SessionGroupTabsByFolder"),
            IsCheckable = true,
            IsChecked = vm.Connection.GroupTabsByFolder
        };
        // Hung off the checked state, not Click: the UIA Toggle pattern flips IsChecked without
        // raising Click, which would leave the tick saying one thing and the strip another.
        groupItem.Checked += (_, _) => vm.Connection.GroupTabsByFolder = true;
        groupItem.Unchecked += (_, _) => vm.Connection.GroupTabsByFolder = false;
        menu.Items.Add(groupItem);

        if (vm.Connection.HasRecentlyClosedTab)
        {
            int closedCount = vm.Connection.MostRecentlyClosedCount;
            var reopenItem = new MenuItem
            {
                Header = closedCount > 1
                    ? vm.GetLocalizer().Format("SessionReopenClosedTabs", closedCount)
                    : vm.Localize("SessionReopenClosedTab"),
                InputGestureText = vm.Localize("SessionReopenClosedTabGesture"),
            };
            reopenItem.Click += (_, _) => vm.Session.ReopenClosedTab();
            menu.Items.Add(reopenItem);
        }

        if (!session.HasTabGroup)
        {
            return;
        }

        var collapseItem = new MenuItem
        {
            Header = vm.Localize(session.IsTabGroupCollapsed ? "SessionExpandTabGroup" : "SessionCollapseTabGroup"),
            Command = vm.Connection.ToggleTabGroupCollapsedCommand,
            CommandParameter = session
        };
        menu.Items.Add(collapseItem);

        if (vm.Connection.HasOtherTabGroup(session))
        {
            menu.Items.Add(new MenuItem
            {
                Header = vm.Localize("SessionCollapseOtherTabGroups"),
                Command = vm.Connection.CollapseOtherTabGroupsCommand,
                CommandParameter = session
            });
        }
    }

    // ── Close ────────────────────────────────────────────────────────

    private static void AppendCloseItem(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm)
    {
        var closeItem = new MenuItem
        {
            // Named for what it does. It read "Disconnect" on a session tab, and a user who meant
            // to keep the tab for a later reconnect lost it: the item has always closed the tab.
            Header = vm.Localize("SessionCloseTab")
        };
        closeItem.Click += async (_, _) =>
            await vm.Connection.CloseSessionAsync(session, DisconnectReason.UserAction);
        menu.Items.Add(closeItem);
    }

    // ── Connection-only actions (reconnect, duplicate, fullscreen) ──

    private static void AppendSessionActions(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        ISessionTabContextCallbacks callbacks)
    {
        // Reconnect session: close the current tab and re-open the same server.
        // Uses the same flow as the disconnect overlay so the existing tab is
        // always replaced rather than leaving a stale disconnected tab behind.
        var reconnectItem = new MenuItem { Header = vm.Localize("SessionReconnectTab") };
        reconnectItem.Click += (_, _) => vm.Session.ReconnectSession(session);
        menu.Items.Add(reconnectItem);

        // Duplicate session: open a second tab for the same server while
        // keeping the current one. Distinct from "Reconnect".
        var duplicateItem = new MenuItem { Header = vm.Localize("SessionDuplicateTab") };
        duplicateItem.Click += (_, _) => vm.Session.DuplicateSession(session);
        menu.Items.Add(duplicateItem);

        // Fullscreen toggle
        var fullscreenItem = new MenuItem
        {
            Header = vm.Localize("SessionFullscreen"),
            InputGestureText = "F11"
        };
        fullscreenItem.Click += (_, _) => callbacks.ToggleFullscreen();
        menu.Items.Add(fullscreenItem);

        if (session.IsAdHoc && session.AdHocProfileSnapshot is not null)
        {
            var saveAsProfileItem = new MenuItem
            {
                Header = vm.Localize("SessionSaveAsProfile")
            };
            saveAsProfileItem.Click += (_, _) =>
                vm.ServerList.SaveAdHocAsProfileCommand.Execute(session.AdHocProfileSnapshot);
            menu.Items.Add(saveAsProfileItem);
        }
    }

    private static void AppendProfileActions(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        ISessionTabContextCallbacks callbacks)
    {
        // The whole inventory, not the tree's filtered view: with a search typed in the tree, the
        // profile of an open tab is usually filtered out, and these entries vanished with it.
        ServerItemViewModel? serverVm = SessionTabToolTipComposer.FindProfile(vm, session);

        if (serverVm is null)
        {
            return;
        }

        MenuItem editItem = new MenuItem
        {
            Header = vm.Localize("TreeCtxEdit"),
            Command = vm.ServerList.EditServerCommand,
            CommandParameter = serverVm,
            InputGestureText = "Ctrl+E"
        };
        menu.Items.Add(editItem);

        MenuItem copyHostnameItem = new MenuItem
        {
            Header = vm.Localize("TreeCtxCopyHostname"),
            Command = vm.ServerList.CopyHostnameCommand,
            CommandParameter = serverVm
        };
        menu.Items.Add(copyHostnameItem);

        MenuItem copyUsernameItem = new MenuItem
        {
            Header = vm.Localize("TreeCtxCopyUsername"),
            Command = vm.ServerList.CopyUsernameCommand,
            CommandParameter = serverVm,
            IsEnabled = !string.IsNullOrWhiteSpace(serverVm.Username)
        };
        menu.Items.Add(copyUsernameItem);

        MenuItem revealItem = new MenuItem
        {
            Header = vm.Localize("SessionRevealInTree")
        };
        revealItem.Click += (_, _) => callbacks.RevealServerInTree(serverVm.Id);
        menu.Items.Add(revealItem);
    }

    // ── Resolution menu (RDP only) ───────────────────────────────────

    /// <summary>
    /// Builds the RDP Resolution sub-menu. "Match Window" is a nested
    /// sub-menu where the user picks how the dynamic surface should be
    /// shaped (Stretch / 16:9 / 4:3 / 21:9) - these aspect ratio choices
    /// only have a visible effect in the dynamic Match Window mode, so they
    /// no longer live in their own top-level menu. Fixed presets, Custom and
    /// Save-as-default keep their original semantics. Checkmarks reflect the
    /// currently active mode + aspect so the user can see state at a glance.
    /// </summary>
    private static void AppendResolutionMenu(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        ISessionTabContextCallbacks callbacks,
        EmbeddedRdpView rdpView)
    {
        var resolutionMenu = new MenuItem { Header = vm.Localize("SessionResolution") };

        AppendActiveModeHeader(resolutionMenu, rdpView, vm);

        var state = rdpView.GetEffectiveResolutionState();
        var isMatchWindow = state.Mode != Heimdall.Core.Configuration.RdpResolutionMode.Fixed;
        var currentAspect = rdpView.GetCurrentAspectRatio();

        // "Match Window ▸ Stretch / 16:9 / 4:3 / 21:9"
        // Each sub-item sets the aspect and applies MatchWindow in one click;
        // the parent surfaces a checkmark when we're currently in a dynamic mode.
        var matchWindowItem = new MenuItem
        {
            Header = vm.Localize("RdpResolutionMatchWindow"),
            IsChecked = isMatchWindow
        };

        foreach (var (label, tag, ratio) in new[]
        {
            (vm.Localize("SessionAspectStretch"), "Stretch", AspectRatio.Stretch),
            ("16:9", "Ratio16x9", AspectRatio.Ratio16x9),
            ("4:3", "Ratio4x3", AspectRatio.Ratio4x3),
            ("21:9", "Ratio21x9", AspectRatio.Ratio21x9)
        })
        {
            var ratioTag = tag;
            var subItem = new MenuItem
            {
                Header = label,
                IsChecked = isMatchWindow && currentAspect == ratio
            };
            subItem.Click += (_, _) =>
            {
                rdpView.UpdateAspectRatio(ratioTag);
                callbacks.OnResolutionChanged(session.PrimaryPane, ResolutionChoice.MatchWindow);
            };
            matchWindowItem.Items.Add(subItem);
        }

        resolutionMenu.Items.Add(matchWindowItem);

        foreach (var preset in ResolutionPresetCatalog.GetPresets(vm.CurrentSettings))
        {
            var choice = ResolutionChoice.Fixed(preset.Width, preset.Height);
            var item = new MenuItem
            {
                Header = preset.DisplayText,
                Tag = choice,
                IsChecked = !isMatchWindow
                    && state.Width == preset.Width
                    && state.Height == preset.Height,
                ToolTip = rdpView.WouldScaleResolution(preset.Width, preset.Height)
                    ? vm.Localize("RdpResolutionLargerThanWindowTooltip")
                    : null
            };
            item.Click += (_, _) => callbacks.OnResolutionChanged(session.PrimaryPane, choice);
            resolutionMenu.Items.Add(item);
        }

        resolutionMenu.Items.Add(new Separator());

        var customItem = new MenuItem
        {
            Header = vm.Localize("RdpResolutionCustom"),
            Tag = ResolutionChoice.Custom
        };
        customItem.Click += (_, _) => callbacks.OnResolutionChanged(
            session.PrimaryPane,
            ResolutionChoice.Custom);
        resolutionMenu.Items.Add(customItem);

        resolutionMenu.Items.Add(new Separator());

        var saveDefaultItem = new MenuItem
        {
            Header = vm.Localize("RdpResolutionSaveDefaultForServer"),
            Tag = ResolutionChoice.SaveAsDefaultForServer
        };
        saveDefaultItem.Click += (_, _) => callbacks.OnResolutionChanged(
            session.PrimaryPane,
            ResolutionChoice.SaveAsDefaultForServer);
        resolutionMenu.Items.Add(saveDefaultItem);

        menu.Items.Add(resolutionMenu);
    }

    // ── Resolution active-mode header (mirrors toolbar header) ───────

    private static void AppendActiveModeHeader(
        MenuItem resolutionMenu,
        EmbeddedRdpView rdpView,
        MainViewModel vm)
    {
        var headerText = BuildActiveModeHeaderText(rdpView.GetEffectiveResolutionState(), vm.Localize);

        var headerTextBlock = new TextBlock
        {
            Text = headerText,
            FontWeight = FontWeights.SemiBold
        };
        headerTextBlock.SetResourceReference(
            TextBlock.ForegroundProperty,
            "TextSecondaryBrush");

        var headerItem = new MenuItem
        {
            IsEnabled = false,
            IsHitTestVisible = false,
            StaysOpenOnClick = true,
            Header = headerTextBlock
        };

        resolutionMenu.Items.Add(headerItem);
        resolutionMenu.Items.Add(new Separator());
    }

    /// <summary>
    /// Builds the text of the resolution menu's active-mode header.
    /// </summary>
    /// <remarks>
    /// Free of WPF types so the localization contract is testable without a dispatcher. The header
    /// mirrors the toolbar one in <c>EmbeddedRdpView</c>, so both read the same locale keys through
    /// <see cref="EmbeddedRdpView.LocaleKeys"/> rather than each holding its own copy of the text.
    /// </remarks>
    internal static string BuildActiveModeHeaderText(
        RdpEffectiveResolutionState state,
        Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);

        var modeLabel = localize(RdpResolutionModeIndicator.GetModeLocalizationKey(state.Mode));
        var activeModeLabel = localize(ActiveModeLabelKey);
        return RdpResolutionModeIndicator.FormatHeader(
            activeModeLabel,
            modeLabel,
            state.Width,
            state.Height,
            localize(EmbeddedRdpView.LocaleKeys.ResolutionHeaderFormat),
            localize(EmbeddedRdpView.LocaleKeys.ResolutionHeaderWithSizeFormat));
    }

    // ── Detach (branches on split state) ─────────────────────────────

    private static void AppendDetachItem(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        ISessionTabContextCallbacks callbacks)
    {
        if (!session.IsSplit)
        {
            var detachItem = new MenuItem
            {
                Header = vm.Localize("SessionCtxDetach"),
                IsEnabled = session.HostControl is not null
            };
            detachItem.Click += (_, _) => callbacks.DetachSessionToFloatingWindow(session);
            menu.Items.Add(detachItem);
        }
        else
        {
            var detachSecondaryItem = new MenuItem
            {
                Header = vm.Localize("SplitDetachSecondary"),
                IsEnabled = session.SecondaryHostControl is not null
            };
            detachSecondaryItem.Click += (_, _) => callbacks.DetachSecondaryToFloatingWindow(session);
            menu.Items.Add(detachSecondaryItem);
        }
    }

    // ── Transcript toggle (SSH only) ─────────────────────────────────

    private static void AppendTranscriptItem(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        EmbeddedSshView sshView)
    {
        var transcriptItem = new MenuItem
        {
            Header = sshView.IsTranscriptActive
                ? vm.Localize("SessionStopTranscript")
                : vm.Localize("SessionStartTranscript")
        };
        transcriptItem.Click += (_, _) =>
        {
            if (sshView.IsTranscriptActive)
            {
                sshView.StopTranscript();
                vm.StatusText = vm.Localize("SessionTranscriptStopped");
            }
            else
            {
                // The service owns file naming, path resolution, and ACLs; we only surface the result.
                string? logFile = sshView.StartTranscript();
                vm.StatusText = logFile is not null
                    ? string.Format(vm.Localize("SessionTranscriptStarted"), logFile)
                    : vm.Localize("SessionTranscriptStopped");
            }
        };
        menu.Items.Add(transcriptItem);
    }

    // ── Copy output, secrets masked (any readable terminal pane) ─────

    /// <summary>
    /// Adds "Copy output, secrets masked..." when the tab has a terminal pane that can be read.
    /// Hidden otherwise, like the other terminal-only items; the pane is chosen by the same
    /// resolver as the shortcut and the palette entry.
    /// </summary>
    private static void AppendMaskedCopyItem(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        SessionPaneModel? clickedPane)
    {
        if (!vm.MaskedCopy.CanRun(session, clickedPane))
        {
            return;
        }

        var maskedCopyItem = new MenuItem
        {
            Header = vm.Localize("SessionMaskedCopyMenu"),
            InputGestureText = vm.Localize("SessionMaskedCopyGesture"),
        };
        maskedCopyItem.Click += (_, _) => vm.MaskedCopy.Start(session, clickedPane);
        menu.Items.Add(maskedCopyItem);
    }

    // ── Macro record toggle + play submenu (SSH only) ────────────────

    private static void AppendMacroItems(
        ContextMenu menu,
        MainViewModel vm,
        EmbeddedSshView sshView)
    {
        // Macro recording toggle
        var macroRecordItem = new MenuItem
        {
            Header = sshView.IsRecordingMacro
                ? vm.Localize("MacroStopRecording")
                : vm.Localize("MacroStartRecording")
        };
        macroRecordItem.Click += async (_, _) =>
        {
            if (sshView.IsRecordingMacro)
            {
                var entries = sshView.StopRecording();
                if (entries.Count > 0)
                {
                    var name = await vm.DialogService.ShowInputAsync(
                        vm.Localize("MacroNameTitle"),
                        vm.Localize("MacroNamePrompt"));

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        var macro = new TerminalMacro
                        {
                            Name = name,
                            Entries = entries
                        };
                        await MacroService.SaveMacroAsync(macro);
                        vm.StatusText = string.Format(vm.Localize("MacroRecordingStopped"), name);
                    }
                }
            }
            else
            {
                sshView.StartRecording();
                vm.StatusText = vm.Localize("MacroRecordingStarted");
            }
        };
        menu.Items.Add(macroRecordItem);

        // Play macro submenu
        var playMenu = new MenuItem { Header = vm.Localize("MacroPlaySubmenu") };
        var editMenu = new MenuItem { Header = vm.Localize("MacroEditSubmenu") };

        var macros = MacroService.LoadMacros();
        if (macros.Count == 0)
        {
            var emptyItem = new MenuItem
            {
                Header = vm.Localize("MacroNoMacros"),
                IsEnabled = false
            };
            playMenu.Items.Add(emptyItem);
            editMenu.Items.Add(new MenuItem
            {
                Header = vm.Localize("MacroNoMacros"),
                IsEnabled = false
            });
        }
        else
        {
            foreach (var macro in macros)
            {
                var macroItem = new MenuItem { Header = macro.Name, Tag = macro };
                macroItem.Click += async (s, _) =>
                {
                    if (s is MenuItem { Tag: TerminalMacro m })
                    {
                        vm.StatusText = string.Format(vm.Localize("MacroPlaying"), m.Name);
                        try
                        {
                            await sshView.PlayMacro(m, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Warn($"Macro playback failed: {ex.Message}");
                        }
                    }
                };
                playMenu.Items.Add(macroItem);

                var editItem = new MenuItem { Header = macro.Name, Tag = macro };
                editItem.Click += async (s, _) =>
                {
                    if (s is not MenuItem { Tag: TerminalMacro m })
                    {
                        return;
                    }

                    try
                    {
                        var result = await vm.DialogService.ShowMacroEditorAsync(m);
                        if (result is null)
                        {
                            return;
                        }

                        if (result.Action == MacroEditorDialogAction.Delete)
                        {
                            MacroService.DeleteMacro(m.Id);
                            vm.StatusText = string.Format(vm.Localize("MacroDeleted"), m.Name);
                            return;
                        }

                        if (result.Macro is not null)
                        {
                            await MacroService.SaveMacroAsync(result.Macro);
                            vm.StatusText = string.Format(vm.Localize("MacroEdited"), result.Macro.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Warn($"Macro edit failed: {ex.Message}");
                    }
                };
                editMenu.Items.Add(editItem);
            }
        }
        menu.Items.Add(playMenu);
        menu.Items.Add(editMenu);
    }

    // --- Close others / Close to the right ---

    private static void AppendCloseGroupItems(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm)
    {
        var ordered = vm.Connection.ActiveSessions;

        IReadOnlyList<SessionTabViewModel> closeOthersTargets =
            SessionsToCloseOthers(ordered.ToList(), session);
        IReadOnlyList<SessionTabViewModel> closeRightTargets =
            SessionsToCloseToRight(ordered.ToList(), session);

        var closeOthersItem = new MenuItem
        {
            Header = vm.Localize("SessionCloseOthers"),
            IsEnabled = closeOthersTargets.Count > 0
        };
        closeOthersItem.Click += async (_, _) =>
        {
            await vm.Connection.CloseSessionsAsync(
                closeOthersTargets,
                DisconnectReason.UserAction);
        };
        menu.Items.Add(closeOthersItem);

        var closeRightItem = new MenuItem
        {
            Header = vm.Localize("SessionCloseToRight"),
            IsEnabled = closeRightTargets.Count > 0
        };
        closeRightItem.Click += async (_, _) =>
        {
            await vm.Connection.CloseSessionsAsync(
                closeRightTargets,
                DisconnectReason.UserAction);
        };
        menu.Items.Add(closeRightItem);

        if (session.TabGroupKey is { } folder)
        {
            IReadOnlyList<SessionTabViewModel> folderTargets = SessionsInFolder(ordered.ToList(), folder);
            var closeFolderItem = new MenuItem { Header = vm.Localize("SessionCloseFolderTabs") };
            closeFolderItem.Click += async (_, _) =>
            {
                await vm.Connection.CloseSessionsAsync(
                    folderTargets,
                    DisconnectReason.UserAction);
            };
            menu.Items.Add(closeFolderItem);
        }

        // The command was written, tested and reachable from nowhere.
        menu.Items.Add(new MenuItem
        {
            Header = vm.Localize("SessionCloseAllTabs"),
            Command = vm.Connection.CloseAllSessionsCommand,
        });
    }

    // ── Split / merge / unsplit items ────────────────────────────────

    private static void AppendSplitItems(
        ContextMenu menu,
        SessionTabViewModel session,
        MainViewModel vm,
        ISessionTabContextCallbacks callbacks)
    {
        if (!session.IsSplit)
        {
            // "Split..." submenu with orientation sub-items
            var splitMenu = new MenuItem { Header = vm.Localize("SplitMenu") };

            var splitH = new MenuItem { Header = vm.Localize("OrientationHorizontal") };
            splitH.Click += (_, _) => callbacks.RequestSplitSession(session, SplitOrientation.Horizontal);
            splitMenu.Items.Add(splitH);

            var splitV = new MenuItem { Header = vm.Localize("OrientationVertical") };
            splitV.Click += (_, _) => callbacks.RequestSplitSession(session, SplitOrientation.Vertical);
            splitMenu.Items.Add(splitV);

            menu.Items.Add(splitMenu);

            // "Merge with..." submenu - nested per session with orientation sub-items
            var otherSessions = vm.Connection.ActiveSessions
                .Where(s => s != session
                    && s.HostControl is not null
                    && !string.IsNullOrWhiteSpace(s.ServerId))
                .ToList();

            if (otherSessions.Count > 0)
            {
                var mergeMenu = new MenuItem { Header = vm.Localize("SplitMergeWith") };

                foreach (var other in otherSessions)
                {
                    var sourceTab = other;
                    var sessionMenu = new MenuItem { Header = sourceTab.Title };

                    // Merge resolution is session-scoped; profile IDs are shared by duplicate tabs.
                    string mergeId = sourceTab.ServerId;

                    var mergeH = new MenuItem { Header = vm.Localize("OrientationHorizontal") };
                    mergeH.Click += (_, _) => vm.MergeExistingSession(
                        session, mergeId, SplitOrientation.Horizontal);
                    sessionMenu.Items.Add(mergeH);

                    var mergeV = new MenuItem { Header = vm.Localize("OrientationVertical") };
                    mergeV.Click += (_, _) => vm.MergeExistingSession(
                        session, mergeId, SplitOrientation.Vertical);
                    sessionMenu.Items.Add(mergeV);

                    mergeMenu.Items.Add(sessionMenu);
                }

                menu.Items.Add(mergeMenu);
            }
        }
        else
        {
            var unsplit = new MenuItem { Header = vm.Localize("SplitUnsplit") };
            unsplit.Click += (_, _) => callbacks.UnsplitSession(session);
            menu.Items.Add(unsplit);

            var swapItem = new MenuItem { Header = vm.Localize("SplitSwapPanes") };
            swapItem.Click += async (_, _) => await vm.SwapSplitPanesAsync(session);
            menu.Items.Add(swapItem);

            var toggleItem = new MenuItem
            {
                Header = vm.Localize("SplitToggleOrientation"),
                InputGestureText = "Ctrl+Shift+O"
            };
            toggleItem.Click += (_, _) => vm.ToggleSplitOrientation(session);
            menu.Items.Add(toggleItem);

            var closeSecItem = new MenuItem { Header = vm.Localize("SplitCloseSecondary") };
            closeSecItem.Click += (_, _) => vm.CloseSecondaryPaneCommand.Execute(session);
            menu.Items.Add(closeSecItem);
        }
    }
}
