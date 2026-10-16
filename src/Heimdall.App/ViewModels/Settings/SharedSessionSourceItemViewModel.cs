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

using CommunityToolkit.Mvvm.ComponentModel;

namespace Heimdall.App.ViewModels.Settings;

/// <summary>
/// One shared session source in the settings panel: a name, the MobaXterm file it reads, whether
/// it is on, and what happened when it was last read.
/// </summary>
/// <remarks>
/// The errors and the status are set by the panel, not computed here: whether a name is a
/// duplicate depends on the other rows, and the status comes from the shared provider.
/// </remarks>
public partial class SharedSessionSourceItemViewModel : ObservableObject
{
    /// <summary>
    /// The source identifier, kept across edits: every shared identifier and every post-connect
    /// approval of the source is minted from it.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>The localized reason the name cannot be saved, or null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string? _nameError;

    /// <summary>The localized reason the path cannot be saved, or null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPathError))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string? _pathError;

    /// <summary>What happened when the source was last read, in words; read-only.</summary>
    [ObservableProperty]
    private string _statusDescription = string.Empty;

    /// <summary>Whether <see cref="StatusDescription"/> reports a source that could not be read.</summary>
    [ObservableProperty]
    private bool _hasStatusError;

    /// <summary>
    /// Whether <see cref="StatusDescription"/> reports sessions that are not what the file holds
    /// now: an offline copy, or a previous version kept.
    /// </summary>
    [ObservableProperty]
    private bool _hasStatusWarning;

    /// <summary>How many findings the import report of the source's last read holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    private int _issueCount;

    /// <summary>Whether the source has an import report worth opening.</summary>
    public bool HasIssues => IssueCount > 0;

    public bool HasNameError => NameError is not null;

    public bool HasPathError => PathError is not null;

    /// <summary>Whether the row may be saved, as last validated by the panel.</summary>
    public bool IsValid => NameError is null && PathError is null;
}
