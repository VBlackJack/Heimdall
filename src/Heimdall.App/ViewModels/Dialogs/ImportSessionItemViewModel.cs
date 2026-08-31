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
using Heimdall.Core.Import;
using Heimdall.Core.Localization;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// Shared row ViewModel used by import preview dialogs.
/// </summary>
public sealed partial class ImportSessionItemViewModel : ObservableObject, IAccessibleItemViewModel
{
    public ImportSessionItemViewModel(
        object sourceCandidate,
        string alias,
        string hostName,
        int? port,
        string? user,
        string? identityFile,
        string? gatewayChain,
        ImportCandidateStatus status,
        LocalizationManager localizer)
    {
        SourceCandidate = sourceCandidate;
        Alias = alias;
        HostName = hostName;
        Port = port;
        User = string.IsNullOrWhiteSpace(user) ? "∅" : user;
        IdentityFile = string.IsNullOrWhiteSpace(identityFile) ? "∅" : identityFile;
        GatewayChain = string.IsNullOrWhiteSpace(gatewayChain) ? "∅" : gatewayChain;
        Status = status;
        StatusDisplay = status switch
        {
            ImportCandidateStatus.New => localizer["StatusImportNew"],
            ImportCandidateStatus.Duplicate => localizer["StatusImportDuplicate"],
            ImportCandidateStatus.Invalid => localizer["StatusImportInvalid"],
            _ => status.ToString()
        };
        IsSelectable = status != ImportCandidateStatus.Invalid;
        AccessibleName = localizer.Format(
            "A11yImportSessionRow", Alias, HostName, StatusDisplay);
        _isSelected = status == ImportCandidateStatus.New;
    }

    [ObservableProperty]
    private bool _isSelected;

    public object SourceCandidate { get; }

    public string Alias { get; }

    public string HostName { get; }

    public int? Port { get; }

    public string User { get; }

    public string IdentityFile { get; }

    public string GatewayChain { get; }

    public ImportCandidateStatus Status { get; }

    public string StatusDisplay { get; }

    public bool IsSelectable { get; }

    /// <inheritdoc />
    public string AccessibleName { get; }

    /// <inheritdoc />
    public string? AccessibleHelpText => null;


    partial void OnIsSelectedChanged(bool value)
    {
        if (!IsSelectable && value)
        {
            IsSelected = false;
        }
    }
}
