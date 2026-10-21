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

using Heimdall.Core.Updates;

namespace Heimdall.Core.Tests;

/// <summary>
/// The four properties of the Inno Setup script the in-app updater silently depends on.
/// Nothing pinned any of them: dropping skipifsilent would launch the application twice,
/// dropping the WizardSilent guard would block a silent update on a message box for ever,
/// a changed AppId would break the install registration the updater probes, and a changed
/// PrivilegesRequired would make the writability probe disagree with where Inno installs.
/// </summary>
public sealed class InstallerScriptGuardTests
{
    [Fact]
    public void InnoSetupScript_KeepsThePropertiesTheUpdaterDependsOn()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "innosetup.iss"));

        // Inno doubles the brace to escape it; the constant carries the single one.
        Assert.Contains("AppId={" + UpdateSource.InnoSetupAppId, script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.Contains("skipifsilent", script, StringComparison.Ordinal);
        Assert.Contains("WizardSilent()", script, StringComparison.Ordinal);
    }

    /// <summary>
    /// An upgrade does not keep a seed file the new version no longer ships.
    /// </summary>
    /// <remarks>
    /// Inno Setup's [Files] only adds and overwrites, so the script deletes the shipped seed
    /// and packs before copying. The MSI needs no entry: its MajorUpgrade removes the
    /// previous product's files before the new ones are installed.
    /// </remarks>
    [Fact]
    public void Installers_DoNotKeepASeedFileAnUpgradeNoLongerShips()
    {
        string inno = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "innosetup.iss"));
        string wix = File.ReadAllText(Path.Combine(RepositoryRoot(), "installer", "Product.wxs"));

        int installDelete = inno.IndexOf("[InstallDelete]", StringComparison.Ordinal);
        Assert.True(installDelete >= 0, "innosetup.iss needs an [InstallDelete] section");
        int nextSection = inno.IndexOf("\n[", installDelete + 1, StringComparison.Ordinal);
        string section = nextSection < 0 ? inno[installDelete..] : inno[installDelete..nextSection];

        Assert.Contains("Type: filesandordirs; Name: \"{app}\\data\\seed\\actions\"", section, StringComparison.Ordinal);
        Assert.Contains("Type: filesandordirs; Name: \"{app}\\data\\packs\"", section, StringComparison.Ordinal);
        Assert.Contains("<MajorUpgrade", wix, StringComparison.Ordinal);
    }

    /// <remarks>
    /// An Inno Setup failure or a missing compiler was a yellow warning while a WiX failure
    /// was fatal, so a publish could ship a release without the one asset the in-app
    /// updater can use.
    /// </remarks>
    [Fact]
    public void BuildScript_TreatsAMissingInstallerAsFatalInReleaseMode()
    {
        string script = File.ReadAllText(Path.Combine(RepositoryRoot(), "Build.ps1"));

        int fatalBranches = script.Split("a Release build cannot publish without its installer").Length - 1;
        Assert.True(fatalBranches >= 2, "both the compiler-missing and the compile-failed branches must be fatal in Release mode");
    }

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException($"Cannot find repository root from {AppContext.BaseDirectory}.");
    }
}
