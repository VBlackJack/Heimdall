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

using System.Globalization;
using System.IO;
using Heimdall.App.Converters;
using Heimdall.App.Localization;
using Heimdall.Core.Localization;
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.App.Tests;

[CollectionDefinition("Session failure stage localization", DisableParallelization = true)]
public sealed class SessionFailureStageLocalizationCollection;

[Collection("Session failure stage localization")]
public sealed class SessionFailureStageToLabelConverterTests
{
    [Theory]
    [InlineData("en", SessionFailureStage.SshPlinkFallback, "Plink fallback")]
    [InlineData("en", SessionFailureStage.SshPipeMode, "Pipe mode")]
    [InlineData("fr", SessionFailureStage.SshPlinkFallback, "Repli Plink")]
    [InlineData("fr", SessionFailureStage.SshPipeMode, "Mode pipe")]
    public async Task Convert_SshSpecificStage_UsesDedicatedLocalizedLabel(
        string locale,
        SessionFailureStage stage,
        string expected)
    {
        LocalizationManager localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        LocalizationSource.Instance.Initialize(localizer);
        SessionFailureStageToLabelConverter converter = new SessionFailureStageToLabelConverter();

        object actual = converter.Convert(stage, typeof(string), null, CultureInfo.InvariantCulture);

        Assert.Equal(expected, actual);
        Assert.NotEqual(localizer["SessionFailureStageGeneric"], actual);
    }
}
