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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// The last hop of the hardware-acceleration setting: the write that actually reaches the
/// control.
/// </summary>
/// <remarks>
/// <para>Turning the presenter's hardware mode off is what took three concurrent sessions from
/// 1145.9 MB to 763.3 MB and from 3898 handles to 3058. Everything above this write was pinned -
/// the settings file, the dialog, the profile resolver - and the write itself was not, so
/// inverting the value or dropping the call left the whole suite green and gave the memory
/// back.</para>
/// <para>The control answers <c>E_FAIL</c> to a numeric variant, so the variant type is asserted
/// as well as the value: a boxed <see cref="bool"/> marshals as <c>VT_BOOL</c>, an <see cref="int"/>
/// does not.</para>
/// </remarks>
public sealed class RdpPresenterSettingsTests
{
    private const string HardwareModeProperty = "EnableHardwareMode";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyPresenterSettings_WritesTheSessionValueAsABoolean(bool hardwareAcceleration)
    {
        var extendedSettings = new FakeExtendedSettings();

        bool applied = RdpActiveXHost.ApplyPresenterSettings(extendedSettings, hardwareAcceleration);

        Assert.True(applied);
        (string Name, object Value) write = Assert.Single(extendedSettings.Writes);
        Assert.Equal(HardwareModeProperty, write.Name);
        Assert.Equal(hardwareAcceleration, Assert.IsType<bool>(write.Value));
    }

    /// <summary>
    /// A control that refuses the property must be reported, not assumed: the MsTscAx default is
    /// hardware mode on, which is the state this setting exists to leave.
    /// </summary>
    [Fact]
    public void ApplyPresenterSettings_ReportsAControlThatRefusesTheProperty()
    {
        var extendedSettings = new FakeExtendedSettings { Result = ErrorFail };

        bool applied = RdpActiveXHost.ApplyPresenterSettings(
            extendedSettings,
            hardwareAcceleration: false);

        Assert.False(applied);
    }

    private const int ErrorFail = unchecked((int)0x80004005);

    private sealed class FakeExtendedSettings : IMsRdpExtendedSettings
    {
        public int Result { get; init; }

        public List<(string Name, object Value)> Writes { get; } = [];

        public int put_Property(string bstrPropertyName, ref object pValue)
        {
            if (Result < 0)
            {
                return Result;
            }

            Writes.Add((bstrPropertyName, pValue));
            return Result;
        }

        public int get_Property(string bstrPropertyName, out object pValue)
        {
            pValue = Writes
                .Where(write => string.Equals(write.Name, bstrPropertyName, StringComparison.Ordinal))
                .Select(write => write.Value)
                .LastOrDefault() ?? false;
            return Result;
        }
    }
}
