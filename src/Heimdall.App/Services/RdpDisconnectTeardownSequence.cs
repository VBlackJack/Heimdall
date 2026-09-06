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

namespace Heimdall.App.Services;

/// <summary>
/// Enforces the RDP ActiveX teardown order required to avoid WinFormsHost
/// airspace flashes: hide host, clear child, disconnect COM, detach sink, dispose.
/// </summary>
public static class RdpDisconnectTeardownSequence
{
    public static void Execute(
        IRdpDisconnectTeardownTarget target,
        DisconnectReason reason,
        Action<string>? logInfo = null,
        Action<string>? logWarn = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        logInfo ??= Core.Logging.FileLogger.Info;
        logWarn ??= Core.Logging.FileLogger.Warn;

        ExecuteStep(
            "Visibility=Collapsed",
            target,
            reason,
            target.CollapseHost,
            logInfo,
            logWarn);
        ExecuteStep(
            "Child=null",
            target,
            reason,
            target.ClearHostChild,
            logInfo,
            logWarn);
        ExecuteStep(
            "Disconnect",
            target,
            reason,
            target.Disconnect,
            logInfo,
            logWarn);
        ExecuteStep(
            "DetachEventSink",
            target,
            reason,
            target.DetachEventSink,
            logInfo,
            logWarn);
        ExecuteStep(
            "Dispose",
            target,
            reason,
            target.DisposeHost,
            logInfo,
            logWarn);
    }

    private static void ExecuteStep(
        string step,
        IRdpDisconnectTeardownTarget target,
        DisconnectReason reason,
        Action action,
        Action<string> logInfo,
        Action<string> logWarn)
    {
        logInfo(
            $"RdpDisconnectTeardownSequence step={step} reason={reason} target={target.TeardownTargetName}");

        try
        {
            action();
        }
        catch (Exception ex)
        {
            // The whole exception, stack included. This catch wraps the five COM
            // teardown steps, the deepest part of the RDP dispose tail, and it was the
            // last place on that path still logging the message alone: the exit
            // NullReferenceException of 2026-09-03 was never located because every
            // catch it could have crossed threw the stack away.
            logWarn(
                $"RdpDisconnectTeardownSequence step={step} failed reason={reason} target={target.TeardownTargetName}: {ex}");
        }
    }
}
