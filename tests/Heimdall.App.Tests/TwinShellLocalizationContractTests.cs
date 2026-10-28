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

using System.Reflection;
using TwinShell.Core.Interfaces;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the shape of TwinShell's localization seam: one way to ask for a string, and no second
/// localization stack beside Heimdall's own.
/// </summary>
/// <remarks>
/// The seam used to carry a <c>GetString(key, fallback)</c> overload. Its only implementation
/// was the bridge to Heimdall's localizer, whose indexer returns the key for a missing entry and
/// never null, so the fallback it was written to honour could not be returned. No caller used it.
/// A fallback overload that cannot fall back is worse than none: the next caller would trust it.
/// </remarks>
public sealed class TwinShellLocalizationContractTests
{
    private const string GetStringName = "GetString";

    [Fact]
    public void TheSeamOffersASingleGetStringAndNoFallbackOverload()
    {
        MethodInfo[] overloads = typeof(ILocalizationService)
            .GetMethods()
            .Where(method => method.Name == GetStringName)
            .ToArray();

        MethodInfo only = Assert.Single(overloads);
        Assert.Single(only.GetParameters());
    }

    [Fact]
    public void TwinShellCoreCarriesNoResourceStringsOfItsOwn()
    {
        Assembly core = typeof(ILocalizationService).Assembly;

        Assert.Null(core.GetType("TwinShell.Core.Services.LocalizationService"));
        Assert.DoesNotContain(
            core.GetManifestResourceNames(),
            name => name.Contains(".Resources.Strings", StringComparison.Ordinal));
    }
}
