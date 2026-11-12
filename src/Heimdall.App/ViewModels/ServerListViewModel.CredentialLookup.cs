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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.ViewModels;

public partial class ServerListViewModel
{
    /// <summary>
    /// Lets the external credential provider be asked for the given imported sessions, after
    /// showing the user the entry, the destination and the gateways of each.
    /// </summary>
    /// <remarks>
    /// Only what was shown is confirmed: at most <see cref="CredentialLookupConfirmation.MaxRowsShown"/>
    /// sessions per round, and a stored session whose row changed while the dialog was open is
    /// left as it was.
    /// </remarks>
    /// <returns>How many sessions were confirmed; zero when none needed it or the user declined.</returns>
    internal async Task<int> ConfirmCredentialLookupAsync(IReadOnlyList<ServerItemViewModel> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        List<ServerItemViewModel> pending = [.. servers
            .Where(server => !server.IsShared && CredentialLookupConfirmation.NeedsConfirmation(server.SourceProfile))
            .DistinctBy(server => server.Id, StringComparer.Ordinal)];
        if (pending.Count == 0)
        {
            return 0;
        }

        List<ServerItemViewModel> targets = [.. pending.Take(CredentialLookupConfirmation.MaxRowsShown)];
        int remaining = pending.Count - targets.Count;
        AppSettings settings = await _configManager.LoadSettingsAsync();
        IReadOnlyList<CredentialLookupRow> rows = CredentialLookupConfirmation.BuildRows(
            targets.Select(server => server.SourceProfile!),
            settings);
        Dictionary<string, CredentialLookupRow> shown = [];
        for (int index = 0; index < targets.Count; index++)
        {
            shown[targets[index].Id] = rows[index];
        }

        string text = CredentialLookupConfirmation.FormatRows(rows, _localizer);
        if (remaining > 0)
        {
            text += Environment.NewLine + Environment.NewLine + _localizer.FormatCount(
                remaining,
                "CredentialLookupConfirmMoreOne",
                "CredentialLookupConfirmMore",
                remaining);
        }

        bool accepted = await _dialogService.ShowConfirmAsync(
            _localizer["ConfirmCredentialLookupTitle"],
            _localizer.Format("ConfirmCredentialLookupMessage", text),
            "warning",
            _localizer["ServerDialogCredentialLookupAllow"],
            _localizer["BtnCancel"]);
        if (!accepted)
        {
            return 0;
        }

        HashSet<string> confirmedIds;
        int changed;
        try
        {
            AppSettings current = await _configManager.LoadSettingsAsync();
            (confirmedIds, changed) = await _configManager.MutateServersAsync(inventory =>
            {
                HashSet<string> done = new(StringComparer.Ordinal);
                int moved = 0;
                foreach (ServerProfileDto dto in inventory.Where(dto => shown.ContainsKey(dto.Id)))
                {
                    if (!CredentialLookupConfirmation.NeedsConfirmation(dto))
                    {
                        continue;
                    }

                    CredentialLookupRow stored = CredentialLookupConfirmation.BuildRows([dto], current)[0];
                    if (!CredentialLookupConfirmation.AreSame(stored, shown[dto.Id]))
                    {
                        moved++;
                        continue;
                    }

                    dto.CredentialLookupConfirmed = true;
                    done.Add(dto.Id);
                }

                return (done, moved);
            });
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Error("Confirming the credential lookup failed", ex);
            StatusMessageRequested?.Invoke(_localizer["StatusCredentialLookupConfirmFailed"]);
            return 0;
        }

        foreach (ServerItemViewModel server in targets.Where(server => confirmedIds.Contains(server.Id)))
        {
            server.ApplyCredentialLookupConfirmed();
        }

        List<string> lines = [];
        if (confirmedIds.Count > 0)
        {
            lines.Add(_localizer.FormatCount(
                confirmedIds.Count,
                "StatusCredentialLookupConfirmedOne",
                "StatusCredentialLookupConfirmed",
                confirmedIds.Count));
        }

        if (changed > 0)
        {
            lines.Add(_localizer.FormatCount(
                changed,
                "StatusCredentialLookupChangedOne",
                "StatusCredentialLookupChanged",
                changed));
        }

        if (remaining > 0)
        {
            lines.Add(_localizer.FormatCount(
                remaining,
                "StatusCredentialLookupRemainingOne",
                "StatusCredentialLookupRemaining",
                remaining));
        }

        StatusMessageRequested?.Invoke(string.Join(" ", lines));
        return confirmedIds.Count;
    }
}
