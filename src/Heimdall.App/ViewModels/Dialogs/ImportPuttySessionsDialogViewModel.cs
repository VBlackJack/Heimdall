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

using Heimdall.App.Services.Import;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;

namespace Heimdall.App.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the PuTTY sessions import preview dialog.
/// </summary>
public sealed class ImportPuttySessionsDialogViewModel(
    LocalizationManager localizer,
    PuttySessionImporter importer) : ImportSessionsPreviewDialogViewModel(localizer)
{
    private readonly PuttySessionImporter _importer = importer;

    public override string DialogTitle => Localizer["DialogTitleImportPuttySessions"];

    public async Task InitializeAsync(PuttySessionParseResult parseResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var assessments = await _importer.ComputeStatusesAsync(parseResult.Candidates, ct).ConfigureAwait(false);
        SetPreviewData(
            assessments.Select(assessment => new ImportSessionItemViewModel(
                assessment.Candidate,
                assessment.Candidate.DisplayName,
                assessment.Candidate.HostName ?? Localizer["LabelImportPuttyHostNameMissing"],
                assessment.Candidate.Port,
                assessment.Candidate.UserName,
                assessment.Candidate.PublicKeyFile,
                null,
                assessment.Status,
                Localizer)),
            parseResult.Diagnostics.Select(BuildDiagnostic));
    }

    protected override Task<ImportOutcome> PerformImportAsync(IReadOnlyList<ImportSessionItemViewModel> selectedItems)
    {
        var selected = selectedItems
            .Where(item => item.Status != ImportCandidateStatus.Invalid)
            .Select(item => item.SourceCandidate)
            .OfType<PuttySessionCandidate>()
            .ToList();
        return _importer.ImportSelectedAsync(selected);
    }

    private ImportSessionDiagnosticViewModel BuildDiagnostic(PuttySessionDiagnostic diagnostic)
    {
        var key = diagnostic.Code switch
        {
            PuttyDiagnosticCode.DefaultSettingsKeySkipped => "DiagPuttyDefaultSettingsKeySkipped",
            PuttyDiagnosticCode.NonSshProtocolIgnored => "DiagPuttyNonSshProtocolIgnored",
            PuttyDiagnosticCode.MissingHostName => "DiagPuttyMissingHostName",
            PuttyDiagnosticCode.InvalidPortNumber => "DiagPuttyInvalidPortNumber",
            PuttyDiagnosticCode.PpkKeyCapturedNotConverted => "DiagPuttyPpkKeyCapturedNotConverted",
            PuttyDiagnosticCode.ProxyCapturedButNotMapped => "DiagPuttyProxyCapturedButNotMapped",
            PuttyDiagnosticCode.PortForwardingsCapturedButNotMapped => "DiagPuttyPortForwardingsCapturedButNotMapped",
            PuttyDiagnosticCode.RemoteCommandCapturedButNotMapped => "DiagPuttyRemoteCommandCapturedButNotMapped",
            _ => null
        };

        var subject = string.IsNullOrWhiteSpace(diagnostic.SessionName)
            ? Localizer["LabelImportPuttyUnknownSession"]
            : diagnostic.SessionName;
        var messageBody = key is null
            ? diagnostic.Code.ToString()
            : Localizer.Format(key, subject, diagnostic.Context ?? string.Empty);

        return new ImportSessionDiagnosticViewModel(
            diagnostic.Level == PuttyDiagnosticLevel.Warning,
            messageBody);
    }
}
