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

internal sealed class DialogFtpsCertificateVerifier(
    LocalizationManager localizer,
    TrustPromptCoordinator coordinator) : IFtpsCertificateVerifier
{
    private readonly LocalizationManager _localizer = localizer;
    private readonly TrustPromptCoordinator _coordinator = coordinator;

    public async Task<FtpsCertificateDecision> VerifyAsync(
        FtpsCertificatePrompt prompt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var app = Application.Current;
        if (app is null)
        {
            FileLogger.Warn(
                $"DialogFtpsCertificateVerifier invoked without Application.Current for {prompt.Host}:{prompt.Port}; rejecting certificate.");
            return FtpsCertificateDecision.Reject;
        }

        if (ct.IsCancellationRequested)
        {
            return FtpsCertificateDecision.Reject;
        }

        if (app.Dispatcher.CheckAccess())
        {
            // The queue needs the dispatcher to display prompts. Bypass it on the UI
            // thread to avoid deadlocking a caller that waits synchronously. This
            // request is not coalesced, but the UI thread serializes its own dialogs.
            return ShowDialog(app, prompt, ct);
        }

        try
        {
            var key = TrustPromptKey.Create(
                TrustPromptKind.FtpsCertificate,
                prompt.Host,
                prompt.Port,
                prompt.PresentedFingerprint);
            return await _coordinator.RequestAsync(
                key,
                displayCt => ShowDialogOnDispatcherAsync(app, prompt, displayCt),
                FtpsCertificateDecision.Reject,
                ct);
        }
        catch (TaskCanceledException)
        {
            return FtpsCertificateDecision.Reject;
        }
    }

    private Task<FtpsCertificateDecision> ShowDialogOnDispatcherAsync(
        Application app,
        FtpsCertificatePrompt prompt,
        CancellationToken ct)
    {
        if (app.Dispatcher.CheckAccess())
        {
            return Task.FromResult(ShowDialog(app, prompt, ct));
        }

        return app.Dispatcher.InvokeAsync(
            () => ShowDialog(app, prompt, ct),
            System.Windows.Threading.DispatcherPriority.Normal,
            ct).Task;
    }

    private FtpsCertificateDecision ShowDialog(
        Application app,
        FtpsCertificatePrompt prompt,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return FtpsCertificateDecision.Reject;
        }

        var viewModel = new FtpsCertificatePromptDialogViewModel(_localizer, prompt);
        var dialog = new FtpsCertificatePromptDialog(_localizer)
        {
            DataContext = viewModel
        };

        if (app.MainWindow is Window { IsLoaded: true } owner)
        {
            dialog.Owner = owner;
        }

        using var registration = ct.CanBeCanceled
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
        return viewModel.Decision ?? FtpsCertificateDecision.Reject;
    }
}
