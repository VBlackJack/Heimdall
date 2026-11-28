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

using System.Globalization;
using System.Windows;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Logging;
using Heimdall.Core.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Services;

/// <summary>
/// Shared helper that builds the opt-in SSH agent signing confirmation callback
/// (<see cref="AppSettings.SshConfirmAgentSigning"/>) and the FIDO2 security-key touch notification
/// callback for all SSH/SFTP/Tunnel/Gateway connection paths.
/// </summary>
internal static class SshAgentSignConfirmHelper
{
    private static readonly SemaphoreSlim PromptGate = new(1, 1);

    /// <summary>
    /// Builds a confirmation gate when <see cref="AppSettings.SshConfirmAgentSigning"/> is enabled,
    /// or returns <see langword="null"/> when disabled.
    /// </summary>
    public static Func<ISshAgentKey, bool>? BuildConfirmCallback(
        AppSettings? settings,
        string targetHost,
        int targetPort,
        LocalizationManager? localizer,
        CancellationToken ct = default)
    {
        if (settings?.SshConfirmAgentSigning != true)
        {
            return null;
        }

        return key => ConfirmAgentSignBlocking(targetHost, targetPort, key, localizer, ct);
    }

    /// <summary>
    /// Builds a non-blocking callback that notifies the user when a FIDO2 (<c>sk-*</c>) hardware
    /// security key is about to sign and may require a physical touch.
    /// </summary>
    public static Action<ISshAgentKey> BuildBeforeSignNotification(
        LocalizationManager? localizer,
        Action<string>? reportStatus)
    {
        return key =>
        {
            if (!key.IsSecurityKey)
            {
                return;
            }

            string msg = localizer?["StatusSshTouchSecurityKey"]
                ?? "Touch your hardware security key to authenticate...";
            FileLogger.Info($"[SshAgent] Waiting for FIDO2 security key touch ({key.KeyType}).");
            if (reportStatus is not null)
            {
                reportStatus(msg);
                return;
            }

            var app = Application.Current;
            if (app is not null && !app.Dispatcher.HasShutdownStarted)
            {
                _ = app.Dispatcher.BeginInvoke(() =>
                {
                    if (app.MainWindow?.DataContext is ViewModels.MainViewModel mainVm)
                    {
                        mainVm.StatusText = msg;
                    }
                });
            }
        };
    }

    private static bool ConfirmAgentSignBlocking(
        string targetHost,
        int targetPort,
        ISshAgentKey key,
        LocalizationManager? localizer,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        var app = Application.Current;
        var dialogService = (app as App)?.Services?.GetService<IDialogService>();
        if (app is null || dialogService is null)
        {
            FileLogger.Warn("[SshAgent] Signing confirmation requested without UI dialog service; declining fail-closed.");
            return false;
        }

        string fp = SshAgentKeyFingerprints.Compute(key.PublicKeyBlob);
        string title = localizer?["SshAgentSignConfirmTitle"] ?? "Confirm SSH agent signing";
        string template = localizer?["SshAgentSignConfirmMessage"]
            ?? "Allow SSH agent to sign authentication for {0}:{1}?\n\nKey type: {2}\nFingerprint: {3}\nComment: {4}";
        string message = string.Format(
            CultureInfo.CurrentCulture,
            template,
            targetHost,
            targetPort,
            key.KeyType,
            fp,
            string.IsNullOrWhiteSpace(key.Comment) ? "-" : key.Comment);

        try
        {
            PromptGate.Wait(ct);
            try
            {
                if (ct.IsCancellationRequested)
                {
                    return false;
                }

                if (app.Dispatcher.CheckAccess())
                {
                    return dialogService.ShowConfirmAsync(title, message).GetAwaiter().GetResult();
                }

                return app.Dispatcher
                    .InvokeAsync(() => dialogService.ShowConfirmAsync(title, message))
                    .Task.Unwrap()
                    .WaitAsync(ct)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                PromptGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"[SshAgent] Signing confirmation prompt failed: {ex.Message}");
            return false;
        }
    }
}
