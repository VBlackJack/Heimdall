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
    private readonly IUpdateOutcomeStore _outcomeStore;

    public UpdateInstallFlow(
        IUpdateService updateService,
        IUpdateInstaller updateInstaller,
        IApplicationLifecycle lifecycle,
        IUpdateOutcomeStore outcomeStore)
    {
        ArgumentNullException.ThrowIfNull(updateService);
        ArgumentNullException.ThrowIfNull(updateInstaller);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(outcomeStore);
        _updateService = updateService;
        _updateInstaller = updateInstaller;
        _lifecycle = lifecycle;
        _outcomeStore = outcomeStore;
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

            // Written BEFORE the relauncher is launched, not after, and that ordering is
            // a specification. The relauncher can be killed the instant it starts, and
            // this process is about to exit; a record written afterwards might never be
            // written at all, in exactly the case that most needs explaining.
            _outcomeStore.WriteAttempt(update.Version.ToString());

            bool launched;
            try
            {
                launched = _updateInstaller.BeginInstall(package);
            }
            catch (Exception ex)
            {
                // A throw is a launch that did not happen, and it must be treated as one:
                // the record is cleared below, or the next startup explains a failure
                // that never started and the user reads "download failed" now.
                FileLogger.WarnDetailed("Update install could not start the relauncher", ex);
                launched = false;
            }

            if (!launched)
            {
                // Nothing left this process, so there is nothing to explain later. Leaving
                // the record would make the next ordinary startup announce a failure that
                // never happened.
                _outcomeStore.Clear();
                return UpdateInstallOutcome.InstallLaunchFailed;
            }

            try
            {
                package.TransferCleanupToRelauncher();

                // What the ordinary close would have saved, without its prompts: the
                // shutdown requested below makes the main window's close pass return
                // before it saves anything.
                await _lifecycle.PersistStateAsync();
                _lifecycle.RequestShutdown();
            }
            catch (Exception ex)
            {
                // The relauncher is already out there. It waits for this process, refuses
                // to install over it if it never exits, and records that; the attempt
                // record must stay so the next startup can say so.
                FileLogger.WarnDetailed("Update install: shutdown request failed after the relauncher launched", ex);
            }

            return UpdateInstallOutcome.Started;
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
