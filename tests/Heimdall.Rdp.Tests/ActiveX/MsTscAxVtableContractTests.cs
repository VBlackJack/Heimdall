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
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// Pins the vtable slot the host calls <c>put_UseMultimon</c> through against the type library.
/// </summary>
/// <remarks>
/// <para><c>IMsRdpClientNonScriptable5</c> is declared as an empty marker interface and its one
/// member is reached by reading a function pointer out of the vtable at a hard-coded slot. A
/// wrong slot is not a compile error, not a run-time exception and not a log line: it calls
/// whichever member sits there with a boolean argument, and the control does something else with
/// no way to tell. The event DispIds have had this oracle since the reconnection events were found
/// on the wrong ones; the slot had none.</para>
/// <para>The type library records each member's vtable offset, and a derived interface's offsets
/// include everything it inherits, so the slot can be read straight off it.</para>
/// </remarks>
public sealed class MsTscAxVtableContractTests
{
    private const int RegKindNone = 2;

    private static string TypeLibraryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "mstscax.dll");

    [DllImport("oleaut32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void LoadTypeLibEx(string strTypeLibName, int regKind, out ITypeLib typeLib);

    [Fact]
    public void ThePutUseMultimonSlotMatchesTheTypeLibrary()
    {
        int? slot = ReadVtableSlot(nameof(IMsRdpClientNonScriptable5), "UseMultimon", INVOKEKIND.INVOKE_PROPERTYPUT);

        Assert.True(slot.HasValue, "put_UseMultimon was not found on IMsRdpClientNonScriptable5 in the type library.");
        Assert.Equal(RdpActiveXHost.NonScriptable5PutUseMultimonSlot, slot.Value);
    }

    // Positive control: the reader returns nothing for a member that is not there, so the value
    // above is a reading of the library and not the constant echoed back.
    [Fact]
    public void AMemberTheInterfaceDoesNotHaveHasNoSlot()
    {
        Assert.Null(ReadVtableSlot(nameof(IMsRdpClientNonScriptable5), "NoSuchMember", INVOKEKIND.INVOKE_PROPERTYPUT));
    }

    private static int? ReadVtableSlot(string interfaceName, string memberName, INVOKEKIND invokeKind)
    {
        LoadTypeLibEx(TypeLibraryPath, RegKindNone, out ITypeLib library);

        int typeCount = library.GetTypeInfoCount();
        for (int index = 0; index < typeCount; index++)
        {
            library.GetDocumentation(index, out string name, out _, out _, out _);
            if (!string.Equals(name, interfaceName, StringComparison.Ordinal))
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
                        info.GetDocumentation(description.memid, out string candidate, out _, out _, out _);
                        if (string.Equals(candidate, memberName, StringComparison.Ordinal)
                            && description.invkind == invokeKind)
                        {
                            return description.oVft / IntPtr.Size;
                        }
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

            return null;
        }

        return null;
    }
}
