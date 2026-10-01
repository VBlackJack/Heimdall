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

using Heimdall.Core.Configuration;

namespace Heimdall.App.Services;

public readonly record struct ResolutionPreset(int Width, int Height)
{
    public string Tag => $"{Width}x{Height}";

    public string DisplayText => $"{Width} x {Height}";
}

public static class ResolutionPresetCatalog
{
    public static IReadOnlyList<ResolutionPreset> GetPresets(AppSettings? settings)
    {
        IReadOnlyList<string> configured = settings?.RdpResolutionPresets is { Length: > 0 } presets
            ? presets
            : AppSettings.DefaultRdpResolutionPresets;

        var result = new List<ResolutionPreset>(configured.Count);
        foreach (var preset in configured)
        {
            if (TryParse(preset, out var parsed))
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    public static bool TryParse(string? value, out ResolutionPreset preset)
    {
        preset = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(['x', 'X'], StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height)
            || width <= 0
            || height <= 0)
        {
            return false;
        }

        preset = new ResolutionPreset(width, height);
        return true;
    }
}
