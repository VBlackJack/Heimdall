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

using System.Collections.Generic;
using System.Linq;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Freezes the fact that opening the server dialog and saving cannot change how a local shell
/// elevates.
/// </summary>
/// <remarks>
/// <para>A profile stores elevation twice: the modern <c>ElevationMode</c> and the legacy
/// <c>LocalShellElevated</c> flag that predates it. The launcher reconciles them through
/// <see cref="ServerProfileDto.EffectiveElevationMode"/>. The dialog shows only the mode, and used
/// to seed it from the raw stored value, so a profile that elevates by the legacy flag alone
/// displayed None and had that None written back over it on save.</para>
/// <para>The sweep below is over every combination of the two fields rather than over the one case
/// that was broken, because the defect was not that a case was wrong: it was that two surfaces
/// reconciled the same pair by different rules, and only a sweep says they now agree everywhere.
/// </para>
/// </remarks>
public sealed class ServerDialogElevationFidelityTests
{
    public static TheoryData<ElevationMode, bool> EveryStoredCombination()
    {
        TheoryData<ElevationMode, bool> data = [];
        foreach (ElevationMode mode in System.Enum.GetValues<ElevationMode>())
        {
            data.Add(mode, false);
            data.Add(mode, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStoredCombination))]
    public void OpeningAndSavingNeverChangesHowTheShellElevates(
        ElevationMode storedMode,
        bool storedLegacyFlag)
    {
        ServerProfileDto stored = new()
        {
            Id = "profile",
            DisplayName = "Local shell",
            ConnectionType = "LOCAL",
            ElevationMode = storedMode,
            LocalShellElevated = storedLegacyFlag,
        };

        ServerProfileDto saved = ServerDialogViewModel.FromDto(stored).ToDto();

        Assert.Equal(stored.EffectiveElevationMode, saved.EffectiveElevationMode);
    }

    // The specific case that was losing elevation, stated on its own so a future reader sees what
    // this is about without having to expand the sweep in their head.
    [Fact]
    public void AProfileElevatingByTheLegacyFlagAloneKeepsElevating()
    {
        ServerProfileDto legacy = new()
        {
            Id = "profile",
            DisplayName = "Written before the mode existed",
            ConnectionType = "LOCAL",
            ElevationMode = ElevationMode.None,
            LocalShellElevated = true,
        };

        Assert.Equal(ElevationMode.Auto, legacy.EffectiveElevationMode);

        ServerProfileDto saved = ServerDialogViewModel.FromDto(legacy).ToDto();

        Assert.Equal(ElevationMode.Auto, saved.EffectiveElevationMode);
    }

    // Saving does not merely preserve the behaviour, it also stops the profile depending on the
    // legacy flag: the mode now says what the profile does. Asserted because a fix that kept the
    // pair disagreeing would satisfy the sweep above while leaving the trap in place.
    [Fact]
    public void SavingNormalisesALegacyProfileOntoTheModernField()
    {
        ServerProfileDto legacy = new()
        {
            Id = "profile",
            DisplayName = "Written before the mode existed",
            ConnectionType = "LOCAL",
            ElevationMode = ElevationMode.None,
            LocalShellElevated = true,
        };

        ServerProfileDto saved = ServerDialogViewModel.FromDto(legacy).ToDto();

        Assert.Equal(ElevationMode.Auto, saved.ElevationMode);
        Assert.True(saved.LocalShellElevated);
    }

    // The other direction, and the one that matters for security rather than convenience: turning
    // elevation off in the dialog has to actually turn it off. The legacy flag must not survive the
    // change and quietly reinstate what the user just removed, which is what would happen if the
    // saved flag were taken from the seed instead of derived from the mode the user chose.
    [Fact]
    public void TurningElevationOffInTheDialogActuallyTurnsItOff()
    {
        ServerProfileDto legacy = new()
        {
            Id = "profile",
            DisplayName = "Written before the mode existed",
            ConnectionType = "LOCAL",
            ElevationMode = ElevationMode.None,
            LocalShellElevated = true,
        };

        ServerDialogViewModel dialog = ServerDialogViewModel.FromDto(legacy);
        Assert.Equal(ElevationMode.Auto, dialog.ElevationMode);

        dialog.ElevationMode = ElevationMode.None;
        ServerProfileDto saved = dialog.ToDto();

        Assert.Equal(ElevationMode.None, saved.EffectiveElevationMode);
        Assert.False(saved.LocalShellElevated);
    }

    // Guarding the guard: a sweep over an enum that had collapsed to a single member, or a
    // TheoryData built wrong, would pass while comparing almost nothing.
    [Fact]
    public void TheSweepCoversBothFieldsAcrossEveryMode()
    {
        int modes = System.Enum.GetValues<ElevationMode>().Length;
        Assert.True(modes >= 2, $"ElevationMode has only {modes} member(s), so the sweep is trivial");

        List<object?[]> rows = [.. EveryStoredCombination().Select(row => row)];
        Assert.Equal(modes * 2, rows.Count);
    }
}
