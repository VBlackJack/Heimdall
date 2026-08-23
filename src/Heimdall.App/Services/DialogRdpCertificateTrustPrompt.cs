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

using System.Windows;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.Views.Dialogs;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// Puts the RDP certificate question to the user, on the UI dispatcher.
/// </summary>
/// <remarks>
/// <b>Every path that is not an answer returns <see cref="RdpTrustAnswer.Refuse"/>.</b>
/// No application, a cancelled token, a window closed by its title-bar cross: none of
/// those is approval, and the alternative is opening a session nobody approved. The
/// modelling here follows <see cref="DialogHostKeyVerifier"/>, including the deadlock
/// avoidance when the caller is already on the UI thread.
/// </remarks>
internal sealed class DialogRdpCertificateTrustPrompt(
    LocalizationManager localizer,
    TrustPromptCoordinator coordinator) : IRdpCertificateTrustPrompt
{
    private readonly LocalizationManager _localizer = localizer;
    private readonly TrustPromptCoordinator _coordinator = coordinator;

    /// <inheritdoc />
    public async Task<RdpTrustAnswer> AskAsync(
        RdpCertificatePromptContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        Application? app = Application.Current;
        if (app is null)
        {
            FileLogger.Warn(
                $"DialogRdpCertificateTrustPrompt invoked without Application.Current for "
                + $"{context.Host}; refusing the certificate.");
            return RdpTrustAnswer.Refuse;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RdpTrustAnswer.Refuse;
        }

        if (app.Dispatcher.CheckAccess())
        {
            // The queue needs the dispatcher to display prompts. Bypass it on the UI
            // thread so a caller that waits synchronously cannot deadlock. This request
            // is not coalesced, but the UI thread serializes its own dialogs.
            return ShowDialog(app, context, cancellationToken);
        }

        try
        {
            return await _coordinator.RequestAsync(
                BuildKey(context),
                displayCt => ShowDialogOnDispatcherAsync(app, context, displayCt),
                RdpTrustAnswer.Refuse,
                cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return RdpTrustAnswer.Refuse;
        }
    }

    /// <summary>Builds the key that decides which questions are the same question.</summary>
    /// <param name="context">The certificate being asked about.</param>
    /// <remarks>
    /// <b>The profile is part of the key.</b> RDP trust is stored per profile, so two
    /// profiles that meet the same unknown certificate at the same moment are asking two
    /// different questions. Coalescing them would show one dialog, naming one profile,
    /// and write the answer into both trust sets - durable trust granted from a question
    /// the user was never shown.
    /// <para>
    /// The port is not part of it: the thumbprint already identifies the certificate, and
    /// the same certificate on two ports of one host is the same fact.
    /// </para>
    /// </remarks>
    internal static TrustPromptKey BuildKey(RdpCertificatePromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TrustPromptKey.Create(
            TrustPromptKind.RdpCertificate,
            context.Host,
            port: 0,
            context.Thumbprint,
            scope: context.ProfileId ?? string.Empty);
    }

    private Task<RdpTrustAnswer> ShowDialogOnDispatcherAsync(
        Application app,
        RdpCertificatePromptContext context,
        CancellationToken ct)
    {
        if (app.Dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowDialog(app, context, ct));
        }

        return app.Dispatcher.InvokeAsync(
            () => ShowDialog(app, context, ct),
            System.Windows.Threading.DispatcherPriority.Normal,
            ct).Task;
    }

    private RdpTrustAnswer ShowDialog(
        Application app,
        RdpCertificatePromptContext context,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return RdpTrustAnswer.Refuse;
        }

        RdpCertificatePromptDialogViewModel viewModel = new(_localizer, context);
        RdpCertificatePromptDialog dialog = new() { DataContext = viewModel };

        if (app.MainWindow is Window { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        using CancellationTokenRegistration registration = ct.CanBeCanceled
            ? ct.Register(() =>
            {
                if (dialog.Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                _ = dialog.Dispatcher.BeginInvoke(() =>
                {
                    if (dialog.IsVisible)
                    {
                        dialog.Close();
                    }
                });
            })
            : default;

        _ = dialog.ShowDialog();

        // Null means the window closed without an answer. That is a refusal.
        return viewModel.Answer ?? RdpTrustAnswer.Refuse;
    }
}
