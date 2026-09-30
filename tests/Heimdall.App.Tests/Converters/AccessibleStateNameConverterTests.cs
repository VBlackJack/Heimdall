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

using Heimdall.App.Converters;

namespace Heimdall.App.Tests.Converters;

/// <summary>
/// The Settings tab names carry the error badge and the unsaved-changes dot, which were only seen.
/// </summary>
public sealed class AccessibleStateNameConverterTests
{
    [Theory]
    [InlineData(0, "General")]
    [InlineData(2, "General, errors: 2")]
    public void ATabNameCarriesItsErrorCountOnlyWhenThereIsOne(int count, string expected)
        => Assert.Equal(expected, AccessibleStateNameConverter.Compose("General", count, "{0}, errors: {1}"));

    [Theory]
    [InlineData(false, "Settings tab")]
    [InlineData(true, "Settings tab, unsaved changes")]
    public void TheSettingsTabNameCarriesTheUnsavedDot(bool dirty, string expected)
        => Assert.Equal(expected, AccessibleStateNameConverter.Compose("Settings tab", dirty, "{0}, unsaved changes"));
}
