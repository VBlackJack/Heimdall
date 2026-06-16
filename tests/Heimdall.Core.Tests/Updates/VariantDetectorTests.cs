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

public sealed class VariantDetectorTests
{
    [Fact]
    public void Detect_MarkerPresent_ReturnsSelfContained()
    {
        var detector = new VariantDetector(@"C:\App", _ => true);

        Assert.Equal(BuildVariant.SelfContained, detector.Detect());
    }

    [Fact]
    public void Detect_MarkerAbsent_ReturnsStandard()
    {
        var detector = new VariantDetector(@"C:\App", _ => false);

        Assert.Equal(BuildVariant.Standard, detector.Detect());
    }

    [Fact]
    public void Detect_ProbesMarkerUnderBaseDirectory()
    {
        string? probed = null;
        var detector = new VariantDetector(@"C:\App", path =>
        {
            probed = path;
            return false;
        });

        detector.Detect();

        Assert.NotNull(probed);
        Assert.StartsWith(@"C:\App", probed);
        Assert.EndsWith(@"msedgewebview2.exe", probed);
    }
}
