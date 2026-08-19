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

using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// Pins Heimdall's MsTscAx event declarations against the type library the control is dispatched
/// from.
/// </summary>
/// <remarks>
/// <para>The event interface is a dispatch interface, so a wrong DispId is not a compile error, not
/// a run-time exception, and not a log line: the control looks the member up, fails to find it, and
/// carries on. Two members were declared on the DispIds of unrelated events, and nothing in the
/// product could notice: the control dispatched its real events into them, the argument lists did
/// not match, and the failure went back to a caller that ignores it.</para>
/// <para>Reading the type library is the only oracle that can. It is the same artifact the control
/// is described by, so the assertions below fail if either side moves.</para>
/// </remarks>
public sealed class MsTscAxEventContractTests
{
    private const int RegKindNone = 2;

    private static string TypeLibraryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "mstscax.dll");

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string strTypeLibName, int regKind, out ITypeLib typeLib);

    /// <summary>
    /// Guards the guard: if the type library cannot be read, every assertion below would pass on an
    /// empty map, so the reading itself is asserted first.
    /// </summary>
    [Fact]
    public void TheControlsTypeLibraryIsReadable()
    {
        Assert.True(File.Exists(TypeLibraryPath), $"Type library not found: {TypeLibraryPath}");

        IReadOnlyDictionary<string, int> members = ReadEventMemberIds();

        Assert.NotEmpty(members);
        Assert.Contains("OnConnected", members);
    }

    [Fact]
    public void EveryDeclaredDispIdMatchesTheTypeLibrary()
    {
        IReadOnlyDictionary<string, int> members = ReadEventMemberIds();
        List<string> mismatches = [];
        int checkedMembers = 0;

        foreach (MethodInfo method in typeof(IMsTscAxEvents).GetMethods())
        {
            DispIdAttribute? declared = method.GetCustomAttribute<DispIdAttribute>();
            Assert.True(declared is not null, $"{method.Name} carries no DispId attribute.");

            if (!members.TryGetValue(method.Name, out int actual))
            {
                mismatches.Add($"{method.Name}: not a member of the type library interface");
                continue;
            }

            checkedMembers++;
            if (declared!.Value != actual)
            {
                mismatches.Add($"{method.Name}: declared {declared.Value}, type library says {actual}");
            }
        }

        Assert.Empty(mismatches);

        // The interface is small and hand-written, so a silent drop to zero compared members would
        // otherwise read as a pass.
        Assert.Equal(typeof(IMsTscAxEvents).GetMethods().Length, checkedMembers);
    }

    /// <summary>
    /// The two members this test was written for, asserted by value rather than only by agreement,
    /// so a future edit cannot satisfy the comparison above by moving both sides together.
    /// </summary>
    [Theory]
    [InlineData("OnAutoReconnecting", 17)]
    [InlineData("OnAutoReconnected", 33)]
    [InlineData("OnConnected", 2)]
    [InlineData("OnDisconnected", 4)]
    [InlineData("OnLoginComplete", 3)]
    [InlineData("OnFatalError", 10)]
    [InlineData("OnRemoteDesktopSizeChange", 12)]
    public void TheDeclaredDispIdIsTheExpectedValue(string methodName, int expected)
    {
        MethodInfo method = Assert.Single(
            typeof(IMsTscAxEvents).GetMethods(),
            candidate => candidate.Name == methodName);
        DispIdAttribute attribute = Assert.IsType<DispIdAttribute>(
            method.GetCustomAttribute<DispIdAttribute>());

        Assert.Equal(expected, attribute.Value);
        Assert.Equal(expected, ReadEventMemberIds()[methodName]);
    }

    /// <summary>
    /// The DispIds being right is not enough: the reconnection verdict travels in an out parameter,
    /// and the control reads it as an integer state rather than a boolean.
    /// </summary>
    [Fact]
    public void OnAutoReconnectingWritesAnIntegerStateBack()
    {
        MethodInfo method = Assert.Single(
            typeof(IMsTscAxEvents).GetMethods(),
            candidate => candidate.Name == "OnAutoReconnecting");
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(int), parameters[0].ParameterType);
        Assert.Equal(typeof(int), parameters[1].ParameterType);
        Assert.True(parameters[2].IsOut, "The reconnection verdict must be an out parameter.");
        Assert.Equal(
            typeof(AutoReconnectContinueState).MakeByRefType(),
            parameters[2].ParameterType);
    }

    /// <summary>
    /// The values written into that parameter, pinned against the control's own enum.
    /// </summary>
    /// <remarks>
    /// Zero meaning "keep reconnecting" is what made the previous boolean inverted rather than
    /// merely mistyped, so the numbering is asserted rather than assumed.
    /// </remarks>
    [Fact]
    public void TheContinueStateValuesMatchTheTypeLibrary()
    {
        IReadOnlyDictionary<string, int> values = ReadEnumValues("autoReconnectContinue");

        Assert.Equal(
            (int)AutoReconnectContinueState.Automatic,
            values["autoReconnectContinueAutomatic"]);
        Assert.Equal(
            (int)AutoReconnectContinueState.Stop,
            values["autoReconnectContinueStop"]);
        Assert.Equal(
            (int)AutoReconnectContinueState.Manual,
            values["autoReconnectContinueManual"]);
        Assert.Equal(0, (int)AutoReconnectContinueState.Automatic);
    }

    /// <summary>
    /// The polarity, which is the half of this contract the control cannot report on: it accepts
    /// any integer and obeys it.
    /// </summary>
    [Theory]
    [InlineData(true, AutoReconnectContinueState.Stop)]
    [InlineData(false, AutoReconnectContinueState.Automatic)]
    public void TheContinueStatusWrittenBackFollowsTheCancelRequest(
        bool cancelRequested,
        AutoReconnectContinueState expected)
    {
        Assert.Equal(expected, MsTscAxEventSink.ResolveContinueStatus(cancelRequested));
    }

    /// <summary>
    /// Stated as intent rather than as numbers, so a renumbering cannot satisfy the theory above
    /// while reversing what the product asks the control to do.
    /// </summary>
    [Fact]
    public void AskingToCancelStopsTheControlAndNotAskingLetsItContinue()
    {
        Assert.Equal(
            AutoReconnectContinueState.Stop,
            MsTscAxEventSink.ResolveContinueStatus(cancelRequested: true));
        Assert.Equal(
            AutoReconnectContinueState.Automatic,
            MsTscAxEventSink.ResolveContinueStatus(cancelRequested: false));
        Assert.NotEqual(
            MsTscAxEventSink.ResolveContinueStatus(cancelRequested: true),
            MsTscAxEventSink.ResolveContinueStatus(cancelRequested: false));

        // The number the control reads, not only the name Heimdall gives it.
        Assert.Equal(1, (int)MsTscAxEventSink.ResolveContinueStatus(cancelRequested: true));
        Assert.Equal(0, (int)MsTscAxEventSink.ResolveContinueStatus(cancelRequested: false));
    }

    /// <summary>
    /// A wrong parameter count is as silently fatal as a wrong DispId, and just as invisible: the
    /// control's dispatch fails on the argument list and the handler never runs, with nothing
    /// logged. So the arity is compared against the type library too, not only the id.
    /// </summary>
    [Fact]
    public void EveryDeclaredMemberHasTheTypeLibraryArity()
    {
        IReadOnlyDictionary<string, int> arities = ReadEventMemberArities();
        List<string> mismatches = [];
        int compared = 0;

        foreach (MethodInfo method in typeof(IMsTscAxEvents).GetMethods())
        {
            if (!arities.TryGetValue(method.Name, out int expected))
            {
                mismatches.Add($"{method.Name}: not a member of the type library interface");
                continue;
            }

            compared++;
            int declared = method.GetParameters().Length;
            if (declared != expected)
            {
                mismatches.Add($"{method.Name}: declares {declared} parameters, type library says {expected}");
            }
        }

        Assert.Empty(mismatches);
        Assert.Equal(typeof(IMsTscAxEvents).GetMethods().Length, compared);
    }

    private static IReadOnlyDictionary<string, int> ReadEventMemberIds()
        => ReadEventInterface(static description => description.memid);

    private static IReadOnlyDictionary<string, int> ReadEventInterface(Func<FUNCDESC, int> select)
    {
        Dictionary<string, int> members = new(StringComparer.Ordinal);
        LoadTypeLibEx(TypeLibraryPath, RegKindNone, out ITypeLib library);

        int typeCount = library.GetTypeInfoCount();
        for (int index = 0; index < typeCount; index++)
        {
            library.GetDocumentation(index, out string name, out _, out _, out _);
            if (!string.Equals(name, nameof(IMsTscAxEvents), StringComparison.Ordinal))
            {
                continue;
            }

            library.GetTypeInfo(index, out ITypeInfo info);
            info.GetTypeAttr(out IntPtr attributePointer);
            try
            {
                TYPEATTR attributes = Marshal.PtrToStructure<TYPEATTR>(attributePointer);
                for (int function = 0; function < attributes.cFuncs; function++)
                {
                    info.GetFuncDesc(function, out IntPtr functionPointer);
                    try
                    {
                        FUNCDESC description = Marshal.PtrToStructure<FUNCDESC>(functionPointer);
                        info.GetDocumentation(description.memid, out string memberName, out _, out _, out _);
                        members[memberName] = select(description);
                    }
                    finally
                    {
                        info.ReleaseFuncDesc(functionPointer);
                    }
                }
            }
            finally
            {
                info.ReleaseTypeAttr(attributePointer);
            }

            break;
        }

        return members;
    }

    private static IReadOnlyDictionary<string, int> ReadEventMemberArities()
        => ReadEventInterface(static description => description.cParams);

    private static IReadOnlyDictionary<string, int> ReadEnumValues(string memberPrefix)
    {
        Dictionary<string, int> values = new(StringComparer.Ordinal);
        LoadTypeLibEx(TypeLibraryPath, RegKindNone, out ITypeLib library);

        int typeCount = library.GetTypeInfoCount();
        for (int index = 0; index < typeCount; index++)
        {
            library.GetTypeInfo(index, out ITypeInfo info);
            info.GetTypeAttr(out IntPtr attributePointer);
            try
            {
                TYPEATTR attributes = Marshal.PtrToStructure<TYPEATTR>(attributePointer);
                if (attributes.typekind != TYPEKIND.TKIND_ENUM)
                {
                    continue;
                }

                for (int variable = 0; variable < attributes.cVars; variable++)
                {
                    info.GetVarDesc(variable, out IntPtr variablePointer);
                    try
                    {
                        VARDESC description = Marshal.PtrToStructure<VARDESC>(variablePointer);
                        info.GetDocumentation(description.memid, out string memberName, out _, out _, out _);
                        if (memberName.StartsWith(memberPrefix, StringComparison.Ordinal))
                        {
                            values[memberName] = (int)Convert.ToInt64(
                                Marshal.GetObjectForNativeVariant(description.desc.lpvarValue),
                                System.Globalization.CultureInfo.InvariantCulture);
                        }
                    }
                    finally
                    {
                        info.ReleaseVarDesc(variablePointer);
                    }
                }
            }
            finally
            {
                info.ReleaseTypeAttr(attributePointer);
            }
        }

        return values;
    }
}
