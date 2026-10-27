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

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Logging;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Launches configured and auto-detected external tools with consistent
/// validation, placeholder resolution, and user-facing error reporting.
/// </summary>
public sealed class ExternalToolLaunchService
{
    private readonly IDialogService _dialogService;
    private readonly Func<ProcessStartInfo, Process?> _launchProcess;

    public ExternalToolLaunchService(IDialogService dialogService)
        : this(dialogService, Process.Start)
    {
    }

    internal ExternalToolLaunchService(
        IDialogService dialogService,
        Func<ProcessStartInfo, Process?> launchProcess)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(launchProcess);
        _dialogService = dialogService;
        _launchProcess = launchProcess;
    }

    /// <summary>
    /// Launches a user-configured external tool, resolving placeholders from
    /// the supplied server profile when one is available.
    /// </summary>
    public void LaunchConfigured(
        ExternalToolDefinition tool,
        ServerItemViewModel? server,
        Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(localize);

        if (!ValidateExecutable(tool.Name, tool.ExecutablePath, localize))
        {
            return;
        }

        string arguments = tool.Arguments;
        if (server is not null
            && !tool.TryResolveArguments(
                server.RemoteServer,
                server.EffectivePort,
                server.Username,
                out arguments,
                out ArgumentTemplateRejection? rejection,
                serverName: server.DisplayName,
                protocol: server.ConnectionType,
                keyFile: server.SshKeyPath,
                project: server.ProjectName,
                gateway: server.GatewayName))
        {
            ReportRejectedArgument(tool.Name, rejection!, localize);
            return;
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = tool.ExecutablePath,
            Arguments = arguments,
            UseShellExecute = true
        };

        if (!string.IsNullOrWhiteSpace(tool.WorkingDirectory))
        {
            processInfo.WorkingDirectory = tool.WorkingDirectory;
        }

        if (tool.RunAsAdministrator)
        {
            processInfo.Verb = "runas";
        }

        if (tool.RunHidden)
        {
            processInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processInfo.CreateNoWindow = true;
        }

        TryLaunchProcess(
            tool.Name,
            processInfo,
            localize,
            $"External tool launch succeeded: '{tool.Name}' ({tool.ExecutablePath}).",
            $"External tool launch failed: '{tool.Name}' ({tool.ExecutablePath}).");
    }

    /// <summary>
    /// Launches an auto-detected third-party tool (Sysinternals, NirSoft, ...),
    /// resolving server placeholders and handling optional UAC elevation.
    /// </summary>
    public void LaunchDetected(
        ExternalToolInfo tool,
        ServerItemViewModel server,
        Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(localize);

        if (!ValidateExecutable(tool.Name, tool.ExecutablePath, localize))
        {
            return;
        }

        // The same expansion and the same refusal as a configured tool.
        if (!ExternalToolDefinition.TryResolveDetectedArguments(
                tool.Arguments,
                tool.ExecutablePath,
                server.RemoteServer,
                server.EffectivePort.ToString(CultureInfo.InvariantCulture),
                server.Username,
                out string arguments,
                out ArgumentTemplateRejection? rejection))
        {
            ReportRejectedArgument(tool.Name, rejection!, localize);
            return;
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = tool.ExecutablePath,
            Arguments = arguments,
            UseShellExecute = true
        };

        if (tool.RequiresElevation)
        {
            processInfo.Verb = "runas";
        }

        TryLaunchProcess(
            tool.Name,
            processInfo,
            localize,
            $"Detected tool launch succeeded: '{tool.ProviderName}/{tool.Name}' ({tool.ExecutablePath}).",
            $"Detected tool launch failed: '{tool.ProviderName}/{tool.Name}' ({tool.ExecutablePath}).");
    }

    private bool ValidateExecutable(string toolName, string executablePath, Func<string, string> localize)
    {
        if (!InputValidator.IsShellTarget(executablePath))
        {
            return true;
        }

        FileLogger.Warn(
            $"Blocked external tool '{toolName}': executable is a shell target ({executablePath}).");
        _dialogService.ShowWarning(
            localize("AppName"),
            string.Format(
                CultureInfo.CurrentCulture,
                localize("ExternalToolLaunchError"),
                toolName,
                localize("ExternalToolShellTargetBlocked")));
        return false;
    }

    private void ReportRejectedArgument(string toolName, ArgumentTemplateRejection rejection, Func<string, string> localize)
    {
        FileLogger.Warn(
            $"Blocked external tool '{toolName}': the value of {rejection.Token} was refused ({rejection.Reason}).");
        _dialogService.ShowWarning(
            localize("AppName"),
            string.Format(
                CultureInfo.CurrentCulture,
                localize("ExternalToolLaunchError"),
                toolName,
                string.Format(
                    CultureInfo.CurrentCulture,
                    localize(RejectionMessageKey(rejection.Reason)),
                    rejection.Token)));
    }

    /// <summary>The locale key telling the user why a value was refused and what to do.</summary>
    internal static string RejectionMessageKey(ArgumentRejection reason) => reason switch
    {
        ArgumentRejection.LeadingDash => "ExternalToolArgumentLeadingDash",
        _ => "ExternalToolArgumentNeedsQuotes",
    };

    private void TryLaunchProcess(
        string toolName,
        ProcessStartInfo processInfo,
        Func<string, string> localize,
        string successLogMessage,
        string failureLogMessage)
    {
        try
        {
            _launchProcess(processInfo)?.Dispose();
            FileLogger.Info(successLogMessage);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            FileLogger.Info($"UAC cancelled for {toolName}");
        }
        catch (Exception ex)
        {
            FileLogger.Error($"{failureLogMessage} Error type: {ex.GetType().Name}.");
            _dialogService.ShowWarning(
                localize("AppName"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    localize("ExternalToolLaunchError"),
                    toolName,
                    ex.Message));
        }
    }
}
