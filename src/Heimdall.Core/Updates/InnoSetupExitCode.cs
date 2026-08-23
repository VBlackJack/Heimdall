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

namespace Heimdall.Core.Updates;

/// <summary>
/// The exit codes the installer documents, and the only distinction drawn from them.
/// </summary>
/// <remarks>
/// Deliberately on this side of the boundary rather than in the generated script. The
/// script's rule stays as dumb as it can be - a known non-zero code is a failure, full
/// stop - and this taxonomy drives only the WORDING. If the installer technology ever
/// changes, the generated PowerShell does not.
/// <para>
/// The distinction that earns its place: a user who declined the elevation prompt or
/// cancelled the wizard did not suffer a failure, and telling them one occurred would be
/// both wrong and alarming.
/// </para>
/// </remarks>
public static class InnoSetupExitCode
{
    /// <summary>Setup completed.</summary>
    public const int Success = 0;

    /// <summary>Setup could not initialize.</summary>
    public const int InitializationFailed = 1;

    /// <summary>The user cancelled before installation began.</summary>
    public const int CancelledBeforeInstall = 2;

    /// <summary>A fatal error during preparation.</summary>
    public const int FatalPreparationError = 3;

    /// <summary>A fatal error during installation.</summary>
    public const int FatalInstallError = 4;

    /// <summary>The user cancelled during installation.</summary>
    public const int CancelledDuringInstall = 5;

    /// <summary>Setup was terminated by a debugger.</summary>
    public const int TerminatedByDebugger = 6;

    /// <summary>Setup determined it could not proceed.</summary>
    public const int CannotProceed = 7;

    /// <summary>Setup determined it could not proceed after a restart request.</summary>
    public const int CannotProceedAfterRestart = 8;

    /// <summary>
    /// True when the code means the user stopped it rather than something breaking.
    /// </summary>
    /// <remarks>
    /// A declined elevation prompt surfaces here too, and is very probably the most
    /// frequent reason a real update does not apply.
    /// </remarks>
    public static bool IsUserCancellation(int exitCode) =>
        exitCode is CancelledBeforeInstall or CancelledDuringInstall;
}
