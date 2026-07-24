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

using Heimdall.Core.Logging;
using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Shared implementation of the update install flow: download the verified installer, launch the
/// detached relauncher, and request app shutdown on success. Status localization is the caller's
/// responsibility (see <see cref="UpdateInstallOutcomeText"/>).
/// </summary>
internal sealed class UpdateInstallFlow : IUpdateInstallFlow
{
    private readonly IUpdateService _updateService;
    private readonly IUpdateInstaller _updateInstaller;
    private readonly IApplicationLifecycle _lifecycle;

    public UpdateInstallFlow(
        IUpdateService updateService,
        IUpdateInstaller updateInstaller,
        IApplicationLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        ArgumentNullException.ThrowIfNull(updateInstaller);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _updateService = updateService;
        _updateInstaller = updateInstaller;
        _lifecycle = lifecycle;
    }

    public async Task<UpdateInstallOutcome> RunAsync(
        UpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);

        try
        {
            using IVerifiedUpdatePackage package = await _updateService
                .DownloadVerifiedAsync(update, progress, cancellationToken);
            bool launched = _updateInstaller.BeginInstall(package);
            if (launched)
            {
                package.TransferCleanupToRelauncher();
                _lifecycle.RequestShutdown();
                return UpdateInstallOutcome.Started;
            }

            return UpdateInstallOutcome.InstallLaunchFailed;
        }
        catch (OperationCanceledException)
        {
            return UpdateInstallOutcome.Cancelled;
        }
        catch (InvalidOperationException ex)
        {
            FileLogger.Warn($"Update verification failed: {ex.Message}");
            return UpdateInstallOutcome.VerificationFailed;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"Update download failed: {ex.Message}");
            return UpdateInstallOutcome.DownloadFailed;
        }
    }
}
