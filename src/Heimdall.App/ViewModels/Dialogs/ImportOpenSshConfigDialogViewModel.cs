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
/// ViewModel for the OpenSSH config import preview dialog.
/// </summary>
public sealed class ImportOpenSshConfigDialogViewModel(
    LocalizationManager localizer,
    OpenSshConfigImporter importer) : ImportSessionsPreviewDialogViewModel(localizer)
{
    private readonly OpenSshConfigImporter _importer = importer;

    public override string DialogTitle => Localizer["DialogTitleImportOpenSshConfig"];

    public async Task InitializeAsync(OpenSshParseResult parseResult, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parseResult);

        var assessments = await _importer.ComputeStatusesAsync(parseResult.Candidates, ct).ConfigureAwait(false);
        SetPreviewData(
            assessments.Select(assessment => new ImportSessionItemViewModel(
                assessment.Candidate,
                assessment.Candidate.Alias,
                assessment.Candidate.HostName,
                assessment.Candidate.Port,
                assessment.Candidate.User,
                assessment.Candidate.IdentityFile,
                FormatGatewayChain(assessment.GatewayPreviewSteps),
                assessment.Status,
                Localizer)),
            parseResult.Diagnostics.Select(BuildDiagnostic));
    }

    protected override Task<ImportOutcome> PerformImportAsync(IReadOnlyList<ImportSessionItemViewModel> selectedItems)
    {
        var selected = selectedItems
            .Select(item => item.SourceCandidate)
            .OfType<OpenSshImportCandidate>()
            .ToList();
        return _importer.ImportSelectedAsync(selected);
    }

    private ImportSessionDiagnosticViewModel BuildDiagnostic(OpenSshImportDiagnostic diagnostic)
    {
        var key = diagnostic.Code switch
        {
            OpenSshDiagnosticCode.MatchBlockIgnored => "DiagMatchBlockIgnored",
            OpenSshDiagnosticCode.IncludeDirectiveIgnored => "DiagIncludeDirectiveIgnored",
            OpenSshDiagnosticCode.WildcardAliasIgnored => "DiagWildcardAliasIgnored",
            OpenSshDiagnosticCode.UnknownDirectiveIgnored => "DiagUnknownDirectiveIgnored",
            OpenSshDiagnosticCode.InvalidPort => "DiagInvalidPort",
            OpenSshDiagnosticCode.DuplicateAliasInFile => "DiagDuplicateAliasInFile",
            OpenSshDiagnosticCode.ProxyJumpCapturedButNotMapped => "DiagProxyJumpCapturedButNotMapped",
            OpenSshDiagnosticCode.ProxyCommandUnsupported => "WarnProxyJumpUnsupportedProxyCommand",
            OpenSshDiagnosticCode.ProxyJumpMixedWithProxyCommand => "WarnProxyJumpMixedWithProxyCommand",
            OpenSshDiagnosticCode.ProxyJumpTokenSubstitution => "WarnProxyJumpTokenSubstitution",
            OpenSshDiagnosticCode.ProxyJumpCycle => "WarnProxyJumpCycle",
            OpenSshDiagnosticCode.ProxyJumpUnrecognizedSyntax => "WarnProxyJumpUnrecognisedSyntax",
            OpenSshDiagnosticCode.IdentityFileTildeExpanded => "DiagIdentityFileTildeExpanded",
            OpenSshDiagnosticCode.HostNameFallbackToAlias => "DiagHostNameFallbackToAlias",
            OpenSshDiagnosticCode.HostNameTokenSubstitution => "WarnHostNameTokenSubstitution",
            _ => null
        };

        var diagnosticText = key is null
            ? diagnostic.Code.ToString()
            : Localizer.Format(key, diagnostic.Context ?? string.Empty);
        var message = Localizer.Format("LabelImportDiagnosticLine", diagnostic.LineNumber, diagnosticText);

        return new ImportSessionDiagnosticViewModel(
            diagnostic.Level == OpenSshDiagnosticLevel.Warning,
            message,
            diagnostic.LineNumber);
    }

    private string FormatGatewayChain(IReadOnlyList<OpenSshGatewayPreviewStep> steps)
    {
        if (steps.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(" -> ", steps.Select(FormatGatewayStep));
    }

    private string FormatGatewayStep(OpenSshGatewayPreviewStep step)
    {
        var userPrefix = string.IsNullOrWhiteSpace(step.User) ? string.Empty : $"{step.User}@";
        var display = $"{userPrefix}{step.Host}:{step.Port}";
        return string.IsNullOrWhiteSpace(step.ReusedGatewayName)
            ? display
            : $"{display} ({Localizer.Format("InfoProxyJumpReusingGateway", step.ReusedGatewayName)})";
    }
}
