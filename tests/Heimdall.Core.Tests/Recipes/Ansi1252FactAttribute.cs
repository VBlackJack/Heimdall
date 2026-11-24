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

using System.Runtime.InteropServices;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// A fact that runs only where the system ANSI code page is 1252, the page a program reads its command
/// line in. On any other machine it is reported as skipped, not as passed.
/// </summary>
public sealed class Ansi1252FactAttribute : FactAttribute
{
    /// <summary>The Western European ANSI code page.</summary>
    public const uint WesternEuropeanAnsiCodePage = 1252;

    public Ansi1252FactAttribute()
    {
        uint page = SystemAnsiCodePage();
        if (page != WesternEuropeanAnsiCodePage)
        {
            Skip = $"The system ANSI code page is {page}, not {WesternEuropeanAnsiCodePage}.";
        }
    }

    private static uint SystemAnsiCodePage() => OperatingSystem.IsWindows() ? GetACP() : 0;

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
