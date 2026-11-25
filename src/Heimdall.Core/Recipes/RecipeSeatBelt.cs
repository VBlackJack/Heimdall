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

using System.Collections.Frozen;
using Heimdall.Core.Security;

namespace Heimdall.Core.Recipes;

/// <summary>
/// The deny list of interpreters, script hosts and proxy executables a recipe may not name.
/// </summary>
/// <remarks>
/// This list is a seat belt and not a boundary. It stops the obvious mistake (a recipe that starts a
/// shell, an interpreter, or a tool whose job is to start another program), and it is easy to
/// walk around: a copied or renamed binary whose version resource says something else, a program that
/// loads a plug-in, or any tool that is dangerous by itself, passes it. The boundary is the import
/// preview, where the user reads the program and every argument before accepting the recipe.
/// </remarks>
public static class RecipeSeatBelt
{
    private const string ExecutableExtension = ".exe";

    /// <summary>
    /// Suffix of the language-neutral file named by the version resource of many system tools: the
    /// original file name of cmd.exe is <c>Cmd.Exe.MUI</c> (measured on Windows 11).
    /// </summary>
    private const string LanguageNeutralExtension = ".mui";

    private static readonly char[] TrailingTrim = [' ', '.'];

    /// <summary>
    /// File names, without extension, that are refused. Compared ignoring case. This is a second line
    /// of defence and not a boundary: a tool missing from it is not thereby safe, and the import
    /// preview, where the user reads the program and every argument, is what decides. Browsers are
    /// here because their switches start other commands; compilers and script hosts because they run
    /// what they are given.
    /// </summary>
    public static readonly FrozenSet<string> RefusedNames = FrozenSet.ToFrozenSet(
        [
            "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "wsl", "cscript", "wscript", "mshta",
            "python", "pythonw", "py", "node", "perl", "ruby", "php", "java", "javaw",
            "msiexec", "rundll32", "regsvr32", "regsvcs", "regasm", "installutil", "msbuild",
            "certutil", "bitsadmin", "wmic", "forfiles", "schtasks", "sc", "reg", "net", "net1",
            "curl", "ssh", "scp", "sftp", "ftp", "tftp", "git", "tar", "robocopy", "xcopy",
            "expand", "extrac32", "makecab", "hh", "control", "explorer", "conhost", "cmstp",
            "odbcconf", "pcalua", "scriptrunner", "at", "taskkill",
            "msedge", "chrome", "firefox", "brave", "opera", "SyncAppvPublishingServer", "wt",
            "tttracer", "pcwrun", "esentutl", "replace", "mmc", "msdt", "runas", "dotnet",
            "csc", "vbc", "jsc", "ie4uinit", "presentationhost",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when a file name (with or without directory) is on the list, or is a shell target
    /// according to <see cref="InputValidator.IsShellTarget"/>. A trailing <c>.exe</c> or <c>.exe.mui</c> is ignored, and so
    /// are trailing dots and spaces, which Windows ignores too.
    /// </summary>
    public static bool IsRefusedFileName(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath))
            return true;

        if (InputValidator.IsShellTarget(fileNameOrPath))
            return true;

        string name = Path.GetFileName(fileNameOrPath).TrimEnd(TrailingTrim);
        if (name.EndsWith(LanguageNeutralExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^LanguageNeutralExtension.Length];

        if (name.EndsWith(ExecutableExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^ExecutableExtension.Length];

        return RefusedNames.Contains(name);
    }
}
