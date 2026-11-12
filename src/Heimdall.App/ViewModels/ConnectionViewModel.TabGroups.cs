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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

/// <summary>
/// Folder groups in the session tab strip: one strip, the tabs of a folder kept side by side
/// behind a chip carrying the folder's name and colour, each group collapsible like a browser's.
/// </summary>
/// <remarks>
/// <para>A group is derived, never stored: it is the folder the tab's profile lives in, read
/// from the session tree every time. A profile moved to another folder, a folder renamed or
/// recoloured, and a workspace restored on the next launch therefore land in the right group
/// with nothing to migrate or persist.</para>
/// <para>Pinned tabs stay out of every group, as in a browser: they keep the front of the strip
/// and a group may not reach into it.</para>
/// <para>Collapsing is a view state for the lifetime of the window, like pinning.</para>
/// <para>With <see cref="TabGroupsOnOwnRow"/> the folders sit on a row of their own and the strip
/// shows the tabs of one folder at a time: the folder of the selected tab. Which folders are
/// folded is then derived from the selection on every refresh, never toggled by hand.</para>
/// </remarks>
public partial class ConnectionViewModel
{
    private const string TabGroupChipExpandedKey = "SessionTabGroupChipExpandedA11y";
    private const string TabGroupChipCollapsedKey = "SessionTabGroupChipCollapsedA11y";
    private const string TabGroupChipLoneKey = "SessionTabGroupChipLoneA11y";

    private const string TabGroupHeaderShownKey = "SessionTabGroupHeaderShownA11y";
    private const string TabGroupHeaderHiddenKey = "SessionTabGroupHeaderHiddenA11y";
    private const string TabGroupHeaderHiddenFailedKey = "SessionTabGroupHeaderHiddenFailedA11y";
    private const string TabGroupHeaderHiddenFailedOneKey = "SessionTabGroupHeaderHiddenFailedOneA11y";
    private const string TabGroupHeaderHiddenDisconnectedKey = "SessionTabGroupHeaderHiddenDisconnectedA11y";
    private const string TabGroupHeaderHiddenDisconnectedOneKey = "SessionTabGroupHeaderHiddenDisconnectedOneA11y";
    private const string TabGroupHeaderShownOneKey = "SessionTabGroupHeaderShownOneA11y";
    private const string TabGroupHeaderHiddenOneKey = "SessionTabGroupHeaderHiddenOneA11y";

    private const string ToolsTabGroupLabelKey = "SessionTabGroupTools";

    // The tools group is not a folder: it is announced with wording of its own.
    private const string ToolsChipExpandedKey = "SessionTabGroupToolsChipExpandedA11y";
    private const string ToolsChipCollapsedKey = "SessionTabGroupToolsChipCollapsedA11y";
    private const string ToolsChipLoneKey = "SessionTabGroupToolsChipLoneA11y";
    private const string ToolsHeaderShownKey = "SessionTabGroupToolsHeaderShownA11y";
    private const string ToolsHeaderShownOneKey = "SessionTabGroupToolsHeaderShownOneA11y";
    private const string ToolsHeaderHiddenKey = "SessionTabGroupToolsHeaderHiddenA11y";
    private const string ToolsHeaderHiddenOneKey = "SessionTabGroupToolsHeaderHiddenOneA11y";
    private const string ToolsHeaderHiddenFailedKey = "SessionTabGroupToolsHeaderHiddenFailedA11y";
    private const string ToolsHeaderHiddenFailedOneKey = "SessionTabGroupToolsHeaderHiddenFailedOneA11y";
    private const string ToolsHeaderHiddenDisconnectedKey = "SessionTabGroupToolsHeaderHiddenDisconnectedA11y";
    private const string ToolsHeaderHiddenDisconnectedOneKey = "SessionTabGroupToolsHeaderHiddenDisconnectedOneA11y";

    /// <summary>
    /// The key of the group that gathers the tool tabs. A local folder key never starts with a
    /// slash (its segments are joined, empty ones dropped) and a shared one starts with
    /// <see cref="ServerListViewModel.SharedFolderKeyPrefix"/>, so no folder can take this key,
    /// whatever its name.
    /// </summary>
    internal const string ToolsTabGroupKey = "/tools";

    private readonly HashSet<string> _collapsedTabGroups = new(StringComparer.OrdinalIgnoreCase);

    // Own-row mode only: the tab last selected in each folder, so showing a folder again goes back
    // to where the user was in it.
    private readonly Dictionary<string, SessionTabViewModel> _lastActiveTabByGroup = new(StringComparer.OrdinalIgnoreCase);

    // Own-row mode only: the folder shown while the selected tab is in none (a pinned tab, a tab
    // outside any folder), so selecting one does not fold every folder away.
    private string? _lastShownTabGroup;

    private bool _wasTabGroupRowActive;

    private Func<string, SessionTabGroupInfo?>? _tabGroupResolver;

    /// <summary>
    /// True when the session tabs are grouped by the folder of their profile.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabGroupRowActive))]
    private bool _groupTabsByFolder;

    /// <summary>
    /// True when the folders are drawn on a row of their own and the strip shows one folder's tabs
    /// at a time. Only takes effect while <see cref="GroupTabsByFolder"/> is on.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTabGroupRowActive))]
    private bool _tabGroupsOnOwnRow;

    /// <summary>True when the folders are on their own row: grouping on and the option chosen.</summary>
    public bool IsTabGroupRowActive => GroupTabsByFolder && TabGroupsOnOwnRow;

    /// <summary>One header per folder present in the strip, in strip order. Empty outside the own-row mode.</summary>
    public ObservableCollection<SessionTabGroupHeaderViewModel> TabGroupHeaders { get; } = [];

    /// <summary>True when <see cref="TabGroupHeaders"/> holds at least one header.</summary>
    public bool HasTabGroupHeaders => TabGroupHeaders.Count > 0;

    /// <summary>True when the row of folder headers is drawn.</summary>
    public bool ShowTabGroupRow => IsTabGroupRowActive && HasTabGroupHeaders;

    /// <summary>
    /// Answers the folder group of a profile id, or null when the profile is in no folder or is
    /// not in the inventory. Supplied by the shell, which owns the session tree.
    /// </summary>
    internal Func<string, SessionTabGroupInfo?>? TabGroupResolver
    {
        get => _tabGroupResolver;
        set
        {
            _tabGroupResolver = value;
            RefreshTabGroups();
        }
    }

    /// <summary>The folder groups currently collapsed. Exposed for tests.</summary>
    internal IReadOnlyCollection<string> CollapsedTabGroups => _collapsedTabGroups;

    partial void OnGroupTabsByFolderChanged(bool value)
    {
        LeaveTabGroupRowModeIfEnded();
        RefreshTabGroups();
    }

    partial void OnTabGroupsOnOwnRowChanged(bool value)
    {
        LeaveTabGroupRowModeIfEnded();
        RefreshTabGroupFlags();
    }

    // When the folders leave their own row every folder unfolds, and what the mode remembered goes.
    private void LeaveTabGroupRowModeIfEnded()
    {
        bool active = IsTabGroupRowActive;
        if (_wasTabGroupRowActive && !active)
        {
            _collapsedTabGroups.Clear();
            _lastActiveTabByGroup.Clear();
            _lastShownTabGroup = null;
        }

        _wasTabGroupRowActive = active;
    }

    // A tab that becomes the selected one is never left folded away: selecting it, from the
    // keyboard, the tree or a new connection, opens its group. On the own row the whole fold
    // state follows the selection, so it is recomputed whatever the tab.
    partial void OnActiveSessionChanged(SessionTabViewModel? value)
    {
        if (IsTabGroupRowActive)
        {
            RefreshTabGroupFlags();
            return;
        }

        if (value?.TabGroupKey is { } key && _collapsedTabGroups.Remove(key))
        {
            RefreshTabGroupFlags();
        }
    }

    /// <summary>
    /// Re-reads every tab's folder group and puts the strip back in group order. Called when the
    /// session tree changes underneath the strip (a profile moved, a folder renamed or recoloured).
    /// </summary>
    public void RefreshTabGroups()
    {
        foreach (SessionTabViewModel session in ActiveSessions)
        {
            AssignTabGroup(session);
        }

        ApplyPinnedOrdering();
    }

    /// <summary>
    /// Collapses the folder group of <paramref name="session"/>, or expands it when it is
    /// collapsed. Collapsing the group that holds the selected tab first selects the nearest tab
    /// outside it; when there is none, the group stays open, because a strip with nothing to
    /// select would leave the content area showing a tab the strip hides.
    /// </summary>
    [RelayCommand]
    private void ToggleTabGroupCollapsed(SessionTabViewModel? session)
    {
        // On the own row the selection decides what is folded; a hand toggle would be undone.
        if (IsTabGroupRowActive
            || session?.TabGroupKey is not { } key
            || !ActiveSessions.Contains(session))
        {
            return;
        }

        if (_collapsedTabGroups.Remove(key))
        {
            RefreshTabGroupFlags();
            return;
        }

        if (ActiveSession is { } selected && IsInTabGroup(selected, key))
        {
            SessionTabViewModel? fallback = NearestTabOutsideGroup(ActiveSessions, key);
            if (fallback is null)
            {
                return;
            }

            ActiveSession = fallback;
        }

        _collapsedTabGroups.Add(key);
        RefreshTabGroupFlags();
    }

    /// <summary>
    /// Collapses every folder group except the one of <paramref name="session"/>, which is
    /// expanded, so the strip shows that folder's tabs and the tabs outside any folder: switching
    /// from one set of machines to another in one gesture, as mRemoteNG's per-folder panels do.
    /// The selection moves into the folder first when it was elsewhere, so no collapsed folder
    /// is left holding the open tab.
    /// </summary>
    [RelayCommand]
    private void CollapseOtherTabGroups(SessionTabViewModel? session)
    {
        if (IsTabGroupRowActive
            || session?.TabGroupKey is not { } key
            || !ActiveSessions.Contains(session))
        {
            return;
        }

        // Selecting a tab of a collapsed folder unfolds it, and a folder holding the open tab is
        // never collapsed, so once the selection is inside this folder it is open.
        if (ActiveSession is not { } selected || !IsInTabGroup(selected, key))
        {
            ActiveSession = session;
        }

        foreach (SessionTabViewModel tab in ActiveSessions)
        {
            if (tab.TabGroupKey is { } other && !IsInTabGroup(tab, key))
            {
                _collapsedTabGroups.Add(other);
            }
        }

        RefreshTabGroupFlags();
    }

    /// <summary>
    /// Shows the folder <paramref name="key"/> on the own row: selects the tab last selected in it,
    /// or its first tab when none is remembered. The fold state then follows the selection.
    /// </summary>
    [RelayCommand]
    private void ShowTabGroup(string? key)
    {
        if (!IsTabGroupRowActive || string.IsNullOrEmpty(key))
        {
            return;
        }

        SessionTabViewModel? target =
            _lastActiveTabByGroup.TryGetValue(key, out SessionTabViewModel? remembered)
            && ActiveSessions.Contains(remembered)
            && IsInTabGroup(remembered, key)
                ? remembered
                : ActiveSessions.FirstOrDefault(tab => IsInTabGroup(tab, key));

        if (target is not null)
        {
            ActiveSession = target;
        }
    }

    /// <summary>True when the strip holds a folder group other than the one of <paramref name="session"/>.</summary>
    internal bool HasOtherTabGroup(SessionTabViewModel session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.TabGroupKey is { } key
            && ActiveSessions.Any(tab => tab.TabGroupKey is not null && !IsInTabGroup(tab, key));
    }

    /// <summary>
    /// Selects the next tab (<paramref name="step"/> = 1) or the previous one (-1), wrapping round
    /// the strip. A collapsed folder is skipped whole, its chip included, as a browser skips a
    /// collapsed tab group: landing on any of its tabs would unfold it and undo the collapse.
    /// </summary>
    public void SelectAdjacentTab(int step)
    {
        if (ActiveSession is not { } current
            || NextVisibleTab(ActiveSessions, current, step) is not { } next)
        {
            return;
        }

        ActiveSession = next;
    }

    /// <summary>
    /// The tab <paramref name="step"/> places away from <paramref name="current"/>, wrapping round
    /// and skipping every tab of a collapsed folder. Null when there is no other tab to reach.
    /// </summary>
    internal static SessionTabViewModel? NextVisibleTab(
        IReadOnlyList<SessionTabViewModel> ordered,
        SessionTabViewModel current,
        int step)
    {
        int start = -1;
        for (int index = 0; index < ordered.Count; index++)
        {
            if (ReferenceEquals(ordered[index], current))
            {
                start = index;
                break;
            }
        }

        if (start < 0 || step == 0)
        {
            return null;
        }

        int direction = Math.Sign(step);
        for (int offset = 1; offset < ordered.Count; offset++)
        {
            int index = ((start + (direction * offset)) % ordered.Count + ordered.Count) % ordered.Count;
            if (!ordered[index].IsTabGroupCollapsed)
            {
                return ordered[index];
            }
        }

        return null;
    }

    /// <summary>The ordinal that means the last tab rather than the ninth, as in a browser.</summary>
    internal const int LastTabOrdinal = 9;

    /// <summary>
    /// Selects the tab at <paramref name="ordinal"/> (1-based) among those Ctrl+Tab can reach;
    /// <see cref="LastTabOrdinal"/> selects the last of them. With forty tabs open, reaching the
    /// thirty-fifth took thirty-four presses of Ctrl+Tab.
    /// </summary>
    public void SelectTabAtOrdinal(int ordinal)
    {
        if (TabAtOrdinal(ActiveSessions, ordinal) is { } target)
        {
            ActiveSession = target;
        }
    }

    /// <summary>
    /// The tab at <paramref name="ordinal"/> among those not folded in a collapsed group; the last
    /// of them for <see cref="LastTabOrdinal"/>. Null when the strip holds fewer.
    /// </summary>
    internal static SessionTabViewModel? TabAtOrdinal(IReadOnlyList<SessionTabViewModel> ordered, int ordinal)
    {
        List<SessionTabViewModel> reachable = ordered.Where(tab => !tab.IsTabGroupCollapsed).ToList();
        if (reachable.Count == 0 || ordinal < 1 || ordinal > LastTabOrdinal)
        {
            return null;
        }

        if (ordinal == LastTabOrdinal)
        {
            return reachable[^1];
        }

        return ordinal <= reachable.Count ? reachable[ordinal - 1] : null;
    }

    /// <summary>
    /// Moves the active tab one place left (<paramref name="step"/> = -1) or right (1). Kept inside
    /// its pinned block and its folder by <see cref="MoveSession"/>, as a drag is.
    /// </summary>
    public void MoveActiveTab(int step)
    {
        if (ActiveSession is not { } current || step == 0)
        {
            return;
        }

        int index = ActiveSessions.IndexOf(current);
        if (index < 0)
        {
            return;
        }

        MoveSession(current, index + Math.Sign(step));
        ActiveSession = current;
    }

    /// <summary>
    /// Stable order for the strip: pinned tabs first, then the rest with the tabs of each folder
    /// group gathered where the group's first tab stands. Ungrouped tabs keep their place
    /// relative to the groups. With no group assigned this is exactly the pinned partition.
    /// </summary>
    internal static IReadOnlyList<SessionTabViewModel> OrderForStrip(
        IReadOnlyList<SessionTabViewModel> sessions)
    {
        var pinned = new List<SessionTabViewModel>();
        var blocks = new List<List<SessionTabViewModel>>();
        var blocksByKey = new Dictionary<string, List<SessionTabViewModel>>(StringComparer.OrdinalIgnoreCase);

        foreach (SessionTabViewModel session in sessions)
        {
            if (session.IsPinned)
            {
                pinned.Add(session);
                continue;
            }

            if (session.TabGroupKey is not { } key)
            {
                blocks.Add([session]);
                continue;
            }

            if (!blocksByKey.TryGetValue(key, out List<SessionTabViewModel>? block))
            {
                block = [];
                blocksByKey[key] = block;
                blocks.Add(block);
            }

            block.Add(session);
        }

        foreach (List<SessionTabViewModel> block in blocks)
        {
            pinned.AddRange(block);
        }

        return pinned;
    }

    /// <summary>
    /// The tab to select when the group <paramref name="key"/> folds away: the first visible tab
    /// after the group, else the last visible one before it. Null when every visible tab is in
    /// the group.
    /// </summary>
    internal static SessionTabViewModel? NearestTabOutsideGroup(
        IReadOnlyList<SessionTabViewModel> ordered,
        string key)
    {
        int last = -1;
        int first = -1;
        for (int index = 0; index < ordered.Count; index++)
        {
            if (IsInTabGroup(ordered[index], key))
            {
                first = first < 0 ? index : first;
                last = index;
            }
        }

        if (first < 0)
        {
            return null;
        }

        for (int index = last + 1; index < ordered.Count; index++)
        {
            if (!IsInTabGroup(ordered[index], key) && !ordered[index].IsHiddenInCollapsedGroup)
            {
                return ordered[index];
            }
        }

        for (int index = first - 1; index >= 0; index--)
        {
            if (!IsInTabGroup(ordered[index], key) && !ordered[index].IsHiddenInCollapsedGroup)
            {
                return ordered[index];
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="key"/> is the key of the group that gathers the tool tabs.</summary>
    internal static bool IsToolsTabGroup(string? key)
        => string.Equals(key, ToolsTabGroupKey, StringComparison.Ordinal);

    private static bool IsInTabGroup(SessionTabViewModel session, string key)
        => string.Equals(session.TabGroupKey, key, StringComparison.OrdinalIgnoreCase);

    private void AssignTabGroup(SessionTabViewModel session)
    {
        string lookupId = session.ProfileLookupServerId;
        SessionTabGroupInfo? group = GroupTabsByFolder
            && !session.IsPinned
            && _tabGroupResolver is not null
            && !string.IsNullOrWhiteSpace(lookupId)
                ? _tabGroupResolver(lookupId)
                : null;

        // A tool has no folder: the ones the resolver left loose are gathered in one group.
        if (group is null
            && GroupTabsByFolder
            && !session.IsPinned
            && ConnectionTypeCatalog.IsToolConnectionType(session.ConnectionType))
        {
            group = new SessionTabGroupInfo(ToolsTabGroupKey, _localizer[ToolsTabGroupLabelKey], string.Empty);
        }

        session.TabGroupKey = group?.Key;
        session.TabGroupLabel = group?.Label ?? string.Empty;
        session.TabGroupColor = group?.Color ?? string.Empty;
    }

    /// <summary>
    /// Recomputes which tab heads each group, which are folded away, and what the chip announces.
    /// Touches no tab collection, so it is safe from inside a collection-change notification; the
    /// header list it reconciles is not the one the strip is bound to.
    /// </summary>
    private void RefreshTabGroupFlags()
    {
        bool ownRow = IsTabGroupRowActive;
        _wasTabGroupRowActive = ownRow;

        var present = new List<string>();
        var presentSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SessionTabViewModel session in ActiveSessions)
        {
            if (session.TabGroupKey is { } groupKey && presentSet.Add(groupKey))
            {
                present.Add(groupKey);
            }
        }

        if (ownRow)
        {
            ApplyOwnRowFoldState(present, presentSet);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SessionTabViewModel session in ActiveSessions)
        {
            if (session.TabGroupKey is not { } key)
            {
                session.IsTabGroupHead = false;
                session.ShowsTabGroupChip = false;
                session.IsTabGroupCollapsed = false;
                session.IsHiddenInCollapsedGroup = false;
                session.TabGroupAccessibleName = string.Empty;
                continue;
            }

            bool head = seen.Add(key);
            bool collapsed = _collapsedTabGroups.Contains(key);
            session.IsTabGroupHead = head;
            session.ShowsTabGroupChip = head && !ownRow;
            session.IsTabGroupCollapsed = collapsed;
            // On the own row the head folds away with its group: the header row carries the folder.
            session.IsHiddenInCollapsedGroup = collapsed && (ownRow || !head);
            // The same test the toggle makes: with no tab left to select outside the group, a
            // click on the chip leaves it open, and the chip used to give no reason why.
            bool tools = IsToolsTabGroup(key);
            string chipKey = collapsed
                ? tools ? ToolsChipCollapsedKey : TabGroupChipCollapsedKey
                : NearestTabOutsideGroup(ActiveSessions, key) is null
                    ? tools ? ToolsChipLoneKey : TabGroupChipLoneKey
                    : tools ? ToolsChipExpandedKey : TabGroupChipExpandedKey;
            session.TabGroupAccessibleName = _localizer.Format(chipKey, key);
        }

        // A group that no longer has a tab forgets it was collapsed: the next tab of that folder
        // opens it afresh rather than arriving folded away.
        _collapsedTabGroups.RemoveWhere(key => !seen.Contains(key));

        ReconcileTabGroupHeaders(ownRow);
    }

    /// <summary>
    /// The own-row invariant, recomputed on every refresh rather than driven by events: the folder
    /// shown is that of the selected tab when it is in the strip, else the one last shown while
    /// it is still there, else none; every other folder present is folded.
    /// </summary>
    private void ApplyOwnRowFoldState(List<string> present, HashSet<string> presentSet)
    {
        foreach (string stale in _lastActiveTabByGroup
            .Where(entry => !ActiveSessions.Contains(entry.Value) || !IsInTabGroup(entry.Value, entry.Key))
            .Select(entry => entry.Key)
            .ToList())
        {
            _lastActiveTabByGroup.Remove(stale);
        }

        string? shown = null;
        if (ActiveSession is { } active && active.TabGroupKey is { } activeKey && ActiveSessions.Contains(active))
        {
            shown = activeKey;
            _lastActiveTabByGroup[activeKey] = active;
        }
        else if (_lastShownTabGroup is { } last && presentSet.Contains(last))
        {
            shown = last;
        }

        _lastShownTabGroup = shown;
        _collapsedTabGroups.Clear();
        foreach (string key in present)
        {
            if (shown is null || !string.Equals(key, shown, StringComparison.OrdinalIgnoreCase))
            {
                _collapsedTabGroups.Add(key);
            }
        }
    }

    /// <summary>
    /// Sets the attention and the announced name of a header from the worst state among its tabs
    /// (<paramref name="worst"/>). Touches nothing else: tabs are neither reordered nor refolded.
    /// </summary>
    private void ApplyHeaderAttention(
        SessionTabGroupHeaderViewModel header,
        int count,
        bool shown,
        SessionTabGroupAttention worst)
    {
        SessionTabGroupAttention attention = shown ? SessionTabGroupAttention.None : worst;
        header.Attention = attention;

        string key = header.Key;
        bool tools = IsToolsTabGroup(key);
        header.AccessibleName = attention switch
        {
            _ when shown => _localizer.FormatCount(
                count,
                tools ? ToolsHeaderShownOneKey : TabGroupHeaderShownOneKey,
                tools ? ToolsHeaderShownKey : TabGroupHeaderShownKey,
                key,
                count),
            SessionTabGroupAttention.Failed => _localizer.FormatCount(
                count,
                tools ? ToolsHeaderHiddenFailedOneKey : TabGroupHeaderHiddenFailedOneKey,
                tools ? ToolsHeaderHiddenFailedKey : TabGroupHeaderHiddenFailedKey,
                key,
                count),
            SessionTabGroupAttention.Disconnected => _localizer.FormatCount(
                count,
                tools ? ToolsHeaderHiddenDisconnectedOneKey : TabGroupHeaderHiddenDisconnectedOneKey,
                tools ? ToolsHeaderHiddenDisconnectedKey : TabGroupHeaderHiddenDisconnectedKey,
                key,
                count),
            _ => _localizer.FormatCount(
                count,
                tools ? ToolsHeaderHiddenOneKey : TabGroupHeaderHiddenOneKey,
                tools ? ToolsHeaderHiddenKey : TabGroupHeaderHiddenKey,
                key,
                count),
        };
    }

    /// <summary>The worst tab state of each folder, in one pass over the strip. Empty outside the own-row mode.</summary>
    private Dictionary<string, SessionTabGroupAttention> WorstTabStateByGroup()
    {
        var worst = new Dictionary<string, SessionTabGroupAttention>(StringComparer.OrdinalIgnoreCase);
        if (!IsTabGroupRowActive)
        {
            return worst;
        }

        foreach (SessionTabViewModel tab in ActiveSessions)
        {
            if (tab.TabGroupKey is not { } key)
            {
                continue;
            }

            SessionTabGroupAttention state = tab.HeaderState switch
            {
                SessionTabHeaderState.Failed => SessionTabGroupAttention.Failed,
                SessionTabHeaderState.Disconnected => SessionTabGroupAttention.Disconnected,
                _ => SessionTabGroupAttention.None,
            };
            if (!worst.TryGetValue(key, out SessionTabGroupAttention current) || state > current)
            {
                worst[key] = state;
            }
        }

        return worst;
    }

    /// <summary>Recomputes only the attention of the headers, after a tab changed state.</summary>
    private void RefreshTabGroupAttention()
    {
        if (!IsTabGroupRowActive || TabGroupHeaders.Count == 0)
        {
            return;
        }

        Dictionary<string, SessionTabGroupAttention> worst = WorstTabStateByGroup();
        foreach (SessionTabGroupHeaderViewModel header in TabGroupHeaders)
        {
            ApplyHeaderAttention(
                header,
                header.TabCount,
                header.IsShown,
                worst.GetValueOrDefault(header.Key));
        }
    }

    /// <summary>
    /// Brings <see cref="TabGroupHeaders"/> in line with the folders present in the strip, in place
    /// and by key: the same instance survives an update, a move or a neighbour's removal.
    /// </summary>
    private void ReconcileTabGroupHeaders(bool ownRow)
    {
        var desired = new List<(string Key, SessionTabViewModel Head, int Count)>();
        if (ownRow)
        {
            var indexByKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (SessionTabViewModel session in ActiveSessions)
            {
                if (session.TabGroupKey is not { } key)
                {
                    continue;
                }

                if (indexByKey.TryGetValue(key, out int index))
                {
                    desired[index] = (desired[index].Key, desired[index].Head, desired[index].Count + 1);
                }
                else
                {
                    indexByKey[key] = desired.Count;
                    desired.Add((key, session, 1));
                }
            }
        }

        Dictionary<string, SessionTabGroupAttention> worst = WorstTabStateByGroup();
        for (int position = 0; position < desired.Count; position++)
        {
            (string key, SessionTabViewModel head, int count) = desired[position];
            int current = -1;
            for (int index = position; index < TabGroupHeaders.Count; index++)
            {
                if (string.Equals(TabGroupHeaders[index].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    current = index;
                    break;
                }
            }

            SessionTabGroupHeaderViewModel header;
            if (current < 0)
            {
                header = new SessionTabGroupHeaderViewModel(key);
                TabGroupHeaders.Insert(position, header);
            }
            else
            {
                header = TabGroupHeaders[current];
                if (current != position)
                {
                    TabGroupHeaders.Move(current, position);
                }
            }

            bool shown = !_collapsedTabGroups.Contains(key);
            header.Label = head.TabGroupLabel;
            header.Color = head.TabGroupColor;
            header.TabCount = count;
            header.IsShown = shown;
            ApplyHeaderAttention(header, count, shown, worst.GetValueOrDefault(key));
        }

        while (TabGroupHeaders.Count > desired.Count)
        {
            TabGroupHeaders.RemoveAt(TabGroupHeaders.Count - 1);
        }

        OnPropertyChanged(nameof(HasTabGroupHeaders));
        OnPropertyChanged(nameof(ShowTabGroupRow));
    }
}
