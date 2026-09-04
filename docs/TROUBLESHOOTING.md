<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - Troubleshooting Guide

*Also available in French: [fr/TROUBLESHOOTING.md](fr/TROUBLESHOOTING.md).*

Index of all issues encountered during development and their solutions.

## Table of Contents

1. [RDP Embedded - White Screen](#rdp-embedded-white-screen)
2. [RDP Embedded - Disconnect Code 4360](#rdp-embedded-disconnect-code-4360)
3. [RDP Embedded - COM RCW Crash on Tab Close](#rdp-embedded-com-rcw-crash-on-tab-close)
4. [RDP Embedded - Resize HRESULT Error](#rdp-embedded-resize-hresult-error)
5. [RDP CredUI Autofill - Dialog Not Detected](#rdp-credui-autofill-dialog-not-detected)
6. [SSH - Pageant Keys Not Recognized by SSH.NET](#ssh-pageant-keys-not-recognized)
7. [SSH Terminal - Arrow Keys Not Working](#ssh-terminal-arrow-keys-not-working)
8. [SSH Terminal - No Colors, Strange Characters](#ssh-terminal-no-colors-strange-characters)
9. [SSH Terminal - Cursor Blinks Too Fast](#ssh-terminal-cursor-blinks-too-fast)
10. [WebView2 - DLL Not Found](#webview2-dll-not-found)
11. [Tab Navigation - Tabs Blocked by Active Sessions](#tab-navigation-blocked-by-sessions)
12. [WPF - DynamicResource in BasedOn](#wpf-dynamicresource-in-basedon)
13. [WPF - XAML Click Handler in Style Setter](#wpf-xaml-click-handler-in-style-setter)
14. [Build - Version Number Overflow](#build-version-number-overflow)
15. [Build - Ambiguous Type References](#build-ambiguous-type-references)
16. [TOFU - HostKeyFingerprint on PSCustomObject](#tofu-hostkeyfingerprint-on-pscustomobject)
17. [Passwords - Not Saved After Edit](#passwords-not-saved-after-edit)
18. [Pageant - AGENT_COPYDATA_ID Wrong Value](#pageant-agent_copydata_id-wrong-value)
19. [Pageant - RSA-SHA2 Algorithm Registration](#pageant-rsa-sha2-algorithm-registration)
20. [Pageant - Sign() Returns Raw Bytes Instead of Blob](#pageant-sign-returns-raw-bytes-instead-of-blob)
21. [SFTP - CheckBox Fires During XAML Parse](#sftp-checkbox-fires-during-xaml-parse)
22. [SFTP - Context Menu Intercepted by MainWindow](#sftp-context-menu-intercepted-by-mainwindow)
23. [Citrix - storebrowse.exe Not Found](#citrix-storebrowseexe-not-found)
24. [Theme Switching - Stale Colors After Swap](#theme-switching-stale-colors-after-swap)
25. [Multi-Exec - Broadcast Sends to Wrong Terminals](#multi-exec-broadcast-sends-to-wrong-terminals)
26. [RDP Embedded - Resize Flicker](#rdp-embedded-resize-flicker)
27. [VNC - noVNC Library Unavailable](#vnc-novnc-unavailable)
28. [VNC - WebSocket Proxy Port Conflict](#vnc-websocket-port-conflict)
29. [X11 Forwarding - No Display](#x11-no-display)
30. [Telnet - Connection Hangs](#telnet-connection-hangs)
31. [FTP - Passive Mode Failures](#ftp-passive-mode)
32. [Tab Detach - WebView2 Session Lost](#tab-detach-webview2)
33. [Ephemeral Server - Port 69 Access Denied](#tftp-port-access-denied)
34. [Quick Connect - Ad-Hoc SSH Fails](#quick-connect-ad-hoc-ssh-fails)
35. [RDP Resize - Still Reconnecting (Delta/Debounce Tuning)](#rdp-resize-still-reconnecting-deltadebounce-tuning)
36. [SFTP - Sudo Fallback Permission Denied (Auth Failure)](#sftp-sudo-fallback-auth-failure)
37. [SFTP - Sudo ls Parser Shows Empty Directory](#sftp-sudo-ls-parser-empty)
38. [SFTP - Sudo Fallback Uses Typed Permission Denied Only](#sftp-typed-permission-denied-only)
39. [WebView2 - Side-by-Side Configuration Error (0x800736B1)](#webview2-sxs-error)
40. [HTTP Traversal - Sibling Prefix Bypass](#http-traversal-sibling-prefix)
41. [Tool Tunnel Scan - Few or No Hosts Found](#tool-tunnel-scan-few-hosts)
42. [SSH - TestEnv Gateway Dropdown Empty After Import](#ssh-testenv-gateway-dropdown-empty)
43. [RDP - IMsRdpExtendedSettings Not Reachable via dynamic Property Access](#rdp-extendedsettings-dynamic-access)
44. [SSH - Host Key Unavailable on Plink Fallback](#ssh-host-key-unavailable-plink)
45. [SSH/SFTP - Host Key Mismatch Mid-Session](#ssh-sftp-host-key-mismatch-mid-session)
46. [FTP - Cleartext Credential Warning](#ftp-cleartext-credential-warning)
47. [WinRM Gateway - HTTP 12152 Invalid Server Response](#winrm-gateway-12152)
48. [KeePassXC Credential Provider - Common Gotchas](#keepassxc-credential-provider)
49. [RDP Embedded - Session Cut Off Shortly After Connecting](#rdp-slow-server-cutoff)
50. [Scheduled Task - Ran But Connected Nothing](#scheduled-task-connected-nothing)

---

## RDP Embedded - White Screen

**Symptom**: RDP ActiveX control connects (OnConnected fires) but the display area stays white.

**Root Cause**: WPF `WindowsFormsHost` airspace problem. The ActiveX control's rendering surface is not properly bound to the visible HWND because WPF hasn't flushed its layout pipeline before `Connect()` is called.

**Solution**: Apply the proven layout flush pattern before AND after `Connect()`:
```csharp
// Before Connect()
FormsHost.UpdateLayout();
SurfaceContainer.UpdateLayout();
WinForms.Application.DoEvents();
Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));

// EnsureHostHandle - force handle creation
if (!_rdpHost.IsHandleCreated) { _ = _rdpHost.Handle; }

// After Connect()
FormsHost.UpdateLayout();
WinForms.Application.DoEvents();
```

**Key Insight**: The `FormsHost` MUST be in the visible visual tree with a valid size BEFORE `Connect()`. Use retry loop if surface is not ready (up to 10 attempts, 120ms intervals).

**Files**: `EmbeddedRdpView.xaml.cs` - `FlushLayoutPipeline()`, `BeginConnect()`

---

## RDP Embedded - Disconnect Code 4360

**Symptom**: RDP connects then disconnects after a few seconds with reason code 4360.

**Root Cause**: Code 4360 means "session disconnected" - can be caused by:
1. `UpdateResolution()` called too early after `Connect()` crashes the COM object
2. Server-side licensing or policy issues
3. Dynamic resolution resize during the initial connection handshake

**Solution**: Block `UpdateResolution` calls for 5 seconds after `OnConnected` fires. Skip identical-size updates.

```csharp
_allowResolutionUpdates = false;
// In OnConnected:
_connectedAtUtc = DateTime.UtcNow;
// Enable after 5 second delay:
await Task.Delay(TimeSpan.FromSeconds(5));
_allowResolutionUpdates = true;
```

**Files**: `EmbeddedRdpView.xaml.cs` - `EnableResolutionUpdatesAsync()`

---

## RDP Embedded - COM RCW Crash on Tab Close

**Symptom**: `COM object that has been separated from its underlying RCW cannot be used` when closing a session tab.

**Root Cause**: WPF's `ArrangeOverride` tries to resize the ActiveX control AFTER it's been disposed. The `WindowsFormsHost` still references the COM object during layout.

**Solution**: Hide the `FormsHost` and remove its child BEFORE disposing the COM object:
```csharp
// CRITICAL ORDER:
FormsHost.Visibility = Visibility.Collapsed;  // Stop layout
FormsHost.Child = null;                       // Remove COM from tree
_rdpHost.Disconnect();                        // Then disconnect
_rdpHost.DetachEventSink();                   // Remove event sink
_rdpHost.Dispose();                           // Finally dispose
```

**Files**: `EmbeddedRdpView.xaml.cs` - `Dispose()`

---

## RDP Embedded - Resize HRESULT Error

**Symptom**: `Unexpected HRESULT has been returned from a call to a COM component` during resize.

**Root Cause**: `SetDisplay()` or `UpdateResolution()` called while the RDP session is in a connecting state (not yet fully connected).

**Solution**: Only call `UpdateResolution` when `IsConnected == true` AND after the stabilization delay.

**Files**: `EmbeddedRdpView.xaml.cs` - `OnResizeTimerTick()`

---

## RDP CredUI Autofill - Dialog Not Detected

**Symptom**: CredUI autofill scans find only 8 top-level windows, never detecting the "Windows Security" credential dialog.

**Root Cause**: The CredUI dialog from an embedded ActiveX control is NOT a top-level window - it's a child/owned window spawned by the RDP control's thread. `EnumWindows` only finds top-level windows.

**Solution**: In addition to `EnumWindows`, also scan all threads of the current process with `EnumThreadWindows`:
```csharp
foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
{
    EnumThreadWindows((uint)thread.Id, callback, IntPtr.Zero);
}
```

Also use UI Automation (`System.Windows.Automation`) for modern XAML-based CredUI dialogs, with Win32 `SendMessage`/`BM_CLICK` as fallback for classic dialogs.

When autofill silently fails, enable Debug-level logging for `CredentialAutofill` and inspect the broker enumeration entry added in `1d7c78c`: candidate window titles, process names, and rejection reasons show why a prompt was rejected. These diagnostics include metadata only; credential fields and edit-field contents do not appear in the log.

**Files**: `CredentialAutofill.cs` - `GetVisibleWindows()`, `InjectPassword()`

---

## SSH - Pageant Keys Not Recognized

**Symptom**: `Server rejected the SSH key` or `No suitable authentication method found` even with Pageant running and keys loaded.

**Root Cause**: SSH.NET 2025.1.0 does not have built-in Pageant agent support. The `NoneAuthenticationMethod` fallback doesn't trigger Pageant negotiation.

**Solution**: Two-pronged approach:
1. **Pageant IPC client**: Custom `PageantClient` communicates with Pageant via Win32 shared memory (`CreateFileMapping` + `WM_COPYDATA`). Wraps keys as `IPrivateKeySource` for SSH.NET via `PageantKeyWrapper` + `PageantHostAlgorithm`.
2. **Plink fallback**: When `RequiresPageantFallback()` detects Pageant-only auth, use `PlinkTunnelRunner` for tunnels and `PipeModeSession` for interactive SSH. Plink communicates with Pageant natively.

**Files**: `Pageant/PageantClient.cs`, `SshConnectionFactory.cs`, `ConnectionService.cs`

---

## SSH Terminal - Arrow Keys Not Working

**Symptom**: Arrow keys don't navigate command history in bash. Pressing Up shows `^[[A` instead.

**Root Cause**: ConPTY (`CreatePseudoConsole`) converts VT input sequences to Windows console key events, then reconverts back. This double-conversion breaks arrow key escape sequences.

**Solution**: Use **pipe mode** (NOT ConPTY) for SSH terminals. `PipeModeSession` redirects stdin/stdout directly without a pseudo-console. Combined with plink's `-t` flag (forces remote PTY allocation), VT sequences pass through raw.

```
xterm.js → ESC[A → stdin pipe → plink -t → remote PTY → bash
bash → ESC[A response → stdout pipe → xterm.js
```

**Key Rule**: NEVER use ConPTY for SSH terminals that go through plink. ConPTY is for local shells only.

**Files**: `PipeModeSession.cs`, `ConnectionService.cs` - `ConnectSshViaPlinkAsync()`

---

## SSH Terminal - No Colors, Strange Characters

**Symptom**: Terminal shows raw ANSI escape codes like `[?2004h`, `[0;32m` instead of colors. No cursor.

**Root Cause**: Initial implementation used a WPF `TextBlock` with ANSI stripping. A TextBlock cannot render terminal escape sequences.

**Solution**: Replace with **WebView2 + xterm.js** - the industry standard terminal renderer:
- xterm.js handles ALL VT100/xterm rendering (colors, cursor, scrollback, mouse)
- Binary-safe base64 data transfer between process and xterm.js
- `PostWebMessageAsString` for C# → JS, `WebMessageReceived` for JS → C#

**Files**: `EmbeddedSshView.xaml`, `EmbeddedSshView.xaml.cs`, `Assets/terminal.html`

---

## SSH Terminal - Cursor Blinks Too Fast

**Symptom**: The xterm.js cursor blinks extremely rapidly, much faster than normal.

**Root Cause**: WPF and WebView2 fighting over focus. `GotFocus` and `PreviewMouseDown` handlers on the UserControl call `FocusTerminal()` which sets focus to WebView2, which triggers WPF `LostFocus`, which triggers `GotFocus` again → infinite focus loop.

**Solution**:
1. Remove `GotFocus` and `PreviewMouseDown` handlers
2. Apply focus only ONCE after xterm.js sends the `ready:` message
3. Slow cursor blink via CSS: `animation-duration: 1.2s`

**Files**: `EmbeddedSshView.xaml.cs`, `Assets/terminal.html`

---

## WebView2 - DLL Not Found

**Symptom**: `Unable to load DLL 'WebView2Loader.dll'` at runtime.

**Root Cause**: `WebView2Loader.dll` is a native (non-managed) DLL that `dotnet publish` doesn't copy to the output directory. It gets placed in `lib/webview2/` subfolder instead of alongside the exe.

**Solution**: Copy it explicitly in `Build.ps1` after publish:
```powershell
Copy-Item "src\Heimdall.App\lib\webview2\WebView2Loader.dll" $outputDir -Force
```

**Files**: `Build.ps1`

---

## Tab Navigation - Tabs Blocked by Active Sessions

**Symptom**: When an SSH or RDP session is open, clicking Tunnels/Scheduled/Settings tabs does nothing.

**Root Cause**: Multiple layout architectures were tried:
1. **Sessions as global overlay** (`Panel.ZIndex=10`): Blocks all tabs underneath
2. **Sessions in separate Grid**: Sessions hidden when switching tabs but not restored

**Solution** (from Gemini architecture audit): Sessions live INSIDE the Servers Grid Column 2. When switching tabs, the entire Servers Grid is hidden (`Visibility=Collapsed`) - sessions are NOT destroyed, just visually suspended. Returning to Servers restores them.

Additional fixes:
- Toolbar `Panel.ZIndex=100` ensures clicks reach RadioButtons above WebView2
- `ClipToBounds=True` on content Grid prevents WebView2 overflow
- Focus management: no focus cycling between WPF and WebView2

**Key Rule**: Sessions are children of the Servers tab, never a global overlay.

**Files**: `MainWindow.xaml`, `MainWindow.xaml.cs`

---

## WPF - DynamicResource in BasedOn

**Symptom**: `A 'DynamicResourceExtension' cannot be set on the 'BasedOn' property of type 'Style'`

**Root Cause**: WPF limitation - `BasedOn` only accepts `StaticResource`, not `DynamicResource`.

**Solution**: Replace `BasedOn="{DynamicResource ...}"` with `BasedOn="{StaticResource ...}"`.

---

## WPF - XAML Click Handler in Style Setter

**Symptom**: `Set connectionId threw an exception` when loading a window with a ContextMenu defined inside a Style Setter that uses `Click` event handlers.

**Root Cause**: WPF cannot resolve event handlers in XAML when the ContextMenu is defined inside a `<Setter.Value>` - the handler method is not in scope.

**Solution**: Build the ContextMenu programmatically in code-behind instead of XAML.

**Files**: `MainWindow.xaml.cs` - `OnSessionTabRightClick()`

---

## Build - Version Number Overflow

**Symptom**: `Arithmetic operation resulted in an overflow` during Win32 resource generation.

**Root Cause**: `<Version>2026.031614</Version>` - the segment `031614` exceeds the Win32 version field limit of 65535.

**Solution**: Use separate version properties:
- `<Version>1.0.MMDD.xx</Version>` for Win32 compatibility (AssemblyVersion)
- `<InformationalVersion>YYYY.MMDDxx</InformationalVersion>` for display

**Files**: `Build.ps1`, `Heimdall.App.csproj`

---

## Build - Ambiguous Type References

**Symptom**: `'Point' is an ambiguous reference between 'System.Drawing.Point' and 'System.Windows.Point'`

**Root Cause**: `UseWindowsForms=true` in the csproj imports System.Drawing types alongside System.Windows types.

**Solution**: Fully qualify ambiguous types: `System.Windows.Point`, `System.Windows.DataObject`.

---

## Build - Build.ps1 -SkipTests with dotnet test --no-build

**Symptom**: After running `Build.ps1 -SkipTests`, invoking `dotnet test --no-build` runs stale test assemblies, or fails to find them outright.

**Root Cause**: `-SkipTests` skips both the test pass and the rebuild of test assemblies. `--no-build` then reuses whatever is already on disk.

**Solution**: When iterating on tests after a `-SkipTests` build, run an explicit `dotnet build Heimdall.slnx -c Debug -p:nodeReuse=false` before `dotnet test`.

---

## Build - MSB3026 File Locks from Heimdall / testhost

**Symptom**: `dotnet build` or `dotnet test` emits `MSB3026: Could not copy ...` or `MSB3027: Could not copy ...` on `Heimdall.App.dll`, `Heimdall.App.Tests.dll`, or similar output assemblies. The lock persists across retries.

**Root Cause**: One of two processes is holding the output file open:
- A running `Heimdall.exe` from a previous debug or run session.
- A lingering `testhost.exe` left behind by a previous `dotnet test` invocation (common when the suite was cancelled, crashed, or when parallel build and test runs overlapped).

**Solution**:
1. Kill the offenders:
   ```powershell
   Get-Process Heimdall, testhost -ErrorAction SilentlyContinue | Stop-Process -Force
   ```
2. Rebuild explicitly:
   ```bash
   dotnet build Heimdall.slnx -c Debug -p:nodeReuse=false
   ```
3. Rerun tests sequentially:
   ```bash
   dotnet test Heimdall.slnx --no-build
   ```

**Prevention**: Avoid launching `Build.ps1` or `dotnet test` while a debug instance of Heimdall is still running. If the suite was cancelled mid-run, sweep `testhost.exe` before the next attempt.

---

## TOFU - HostKeyFingerprint on PSCustomObject

**Symptom**: `The property 'HostKeyFingerprint' cannot be found on this object` (Heimdall v1 legacy issue).

**Root Cause**: Gateway/server objects from JSON deserialization are `PSCustomObject`, not C# class instances. New properties added to the C# model don't exist on deserialized objects.

**Solution**: Add `Add-Member` guard before setting:
```powershell
if (-not $gateway.PSObject.Properties['HostKeyFingerprint']) {
    $gateway | Add-Member -MemberType NoteProperty -Name 'HostKeyFingerprint' -Value $null
}
$gateway.HostKeyFingerprint = $fingerprint
```

**Files**: `ConnectionManager.psm1`, `EmbeddedSsh.psm1`, `HeimdallSftpPanel.ps1`

---

## Passwords - Not Saved After Edit

**Symptom**: Password field appears empty when re-opening server edit dialog after saving.

**Root Cause**: `ServerDialogViewModel.ToDto()` didn't map `RdpPassword`/`SshPassword` to `RdpPasswordEncrypted`/`SshPasswordEncrypted` via DPAPI.

**Solution**:
1. Encrypt new passwords in `ToDto()`: `DpapiProvider.Protect(password)`
2. Preserve existing encrypted passwords on edit (if user didn't change the field)
3. Store `ExistingRdpPasswordEncrypted`/`ExistingSshPasswordEncrypted` in ViewModel

**Files**: `ServerDialogViewModel.cs`

---

## Pageant - AGENT_COPYDATA_ID Wrong Value

**Symptom**: `PageantClient` sends a request to Pageant but receives no response. Pageant appears running with keys loaded, but SSH.NET authentication fails with "no suitable method found".

**Root Cause**: The `COPYDATASTRUCT.dwData` field must be set to the exact value `0x804e50ba` (`AGENT_COPYDATA_ID`). Any other value causes Pageant to silently ignore the `WM_COPYDATA` message.

**Solution**: Ensure the constant is correct:
```csharp
private const uint AGENT_COPYDATA_ID = 0x804e50ba;
```

**Files**: `Pageant/PageantClient.cs`

---

## Pageant - RSA-SHA2 Algorithm Registration

**Symptom**: Pageant keys are loaded into SSH.NET but the server rejects authentication. Server logs show "no matching host key type found" or similar.

**Root Cause**: Modern SSH servers disable legacy `ssh-rsa` (SHA-1) and require `rsa-sha2-256` or `rsa-sha2-512`. SSH.NET does not automatically register these algorithms when using custom `IPrivateKeySource` implementations.

**Solution**: Register RSA-SHA2 algorithms on the `ConnectionInfo` before connecting:
```csharp
connectionInfo.HostKeyAlgorithms["rsa-sha2-256"] = ...;
connectionInfo.HostKeyAlgorithms["rsa-sha2-512"] = ...;
```

**Files**: `SshConnectionFactory.cs`, `Pageant/PageantHostAlgorithm.cs`

---

## Pageant - Sign() Returns Raw Bytes Instead of Blob

**Symptom**: SSH authentication starts (key is offered) but fails during the signature exchange. Server rejects the signature.

**Root Cause**: SSH.NET expects the full SSH wire-format signature blob: `[4 bytes: algorithm name length][algorithm name][4 bytes: signature length][signature bytes]`. `PageantClient.SignData()` already returns that full blob from Pageant. Stripping the algorithm prefix, wrapping the blob a second time, or returning only raw signature bytes corrupts the signature.

**Solution**: `PageantHostAlgorithm.Sign()` must return the Pageant blob unchanged. The XML docs and `PageantHostAlgorithmTests.Sign_ReturnsBlobFromAgentUnchanged` pin this contract.

**Files**: `Pageant/PageantHostAlgorithm.cs`

---

## SFTP - CheckBox Fires During XAML Parse

**Symptom**: `NullReferenceException` on startup when `EmbeddedSftpView` is loaded, originating from a `CheckBox.Checked` event handler.

**Root Cause**: A XAML `CheckBox` with `IsChecked="True"` fires the `Checked` event during `InitializeComponent()`, before class fields and other controls are initialized.

**Solution**: Guard event handlers with null checks on fields that may not be initialized yet:
```csharp
private void OnShowHiddenChecked(object sender, RoutedEventArgs e)
{
    if (_sftpClient == null) return; // Not yet initialized
    RefreshDirectory();
}
```

**Files**: `EmbeddedSftpView.xaml.cs`

---

## SFTP - Context Menu Intercepted by MainWindow

**Symptom**: Right-clicking an SFTP session tab opens the generic session context menu (disconnect/close) instead of the SFTP-specific context menu, or causes unexpected behavior.

**Root Cause**: `MainWindow.OnSessionTabRightClick()` intercepts right-click on all session tabs and builds a generic context menu. SFTP tabs have their own context menu (with file operations), but the MainWindow handler fires first and overrides it.

**Solution**: In `OnSessionTabRightClick()`, check the session type and skip context menu creation for SFTP tabs:
```csharp
if (sessionTab.ConnectionType == ConnectionType.Sftp)
    return; // SFTP view handles its own context menu
```

**Files**: `MainWindow.xaml.cs` - `OnSessionTabRightClick()`

---

## Citrix - storebrowse.exe Not Found

**Symptom**: Citrix connection fails immediately with an error indicating `storebrowse.exe` cannot be located.

**Root Cause**: Citrix Workspace App is either not installed or installed in a non-standard location. The default detection path is `%ProgramFiles(x86)%\Citrix\ICA Client\SelfServicePlugin\storebrowse.exe`.

**Solution**:
1. Verify Citrix Workspace App is installed (download from Citrix)
2. If installed in a custom location, set the `CitrixStoreBrowsePath` in Settings > Paths to the full path of `storebrowse.exe`
3. Ensure the Citrix Workspace App version is current - older versions may lack the `storebrowse.exe` CLI

**Files**: `ConnectionService.Citrix.cs`

---

## Theme Switching - Stale Colors After Swap

**Symptom**: After switching themes at runtime, some controls appear with incorrect colors, missing borders, or unstyled backgrounds.

**Root Cause**: WPF `ResourceDictionary` merge order matters. ThemeForge injects the active palette dictionary at runtime, then Heimdall's bridge dictionary maps app brush keys onto ThemeForge color slots. A shared `SolidColorBrush` resource does not live-update a `DynamicResource` assigned to its `Color`; it must be recreated after the palette swap. Converters that resolve brushes via `TryFindResource` at convert time also snapshot the result and do not re-run on theme swap unless their binding inputs change.

**Solution**:
1. Keep the ThemeForge palette injected by `ThemeForge.Theme.ThemeService`; do not statically merge a ThemeForge palette in `App.xaml`
2. Keep `HeimdallThemeBridge.xaml` merged after the active palette, and let `HeimdallThemeService.RefreshHeimdallBridge` re-merge it after each `ApplyTheme`
3. Ensure all theme-dependent styles in `CommonControls.xaml` and `DialogCommonStyles.xaml` use `DynamicResource` for brush properties
4. `BasedOn` must always use `StaticResource` (WPF limitation), but the referenced style itself should use `DynamicResource` for its brush properties
5. For converters that resolve brushes via `TryFindResource`, bind them through a `MultiBinding` that adds `DataContext.ThemeRevision` (ElementName=`MainWindowRoot`) as a trailing trigger value so WPF re-runs the converter after each swap
6. For UI built in code-behind, use `element.SetResourceReference(DP, "BrushKey")` instead of assigning a concrete `Brush` from `FindResource`

**Files**: `Services/HeimdallThemeService.cs`, `Themes/HeimdallThemeBridge.xaml`, `Themes/CommonControls.xaml`, `Themes/DialogCommonStyles.xaml`, `Themes/IconGeometries.xaml`, `Theming/WindowThemeHelper.cs`, NuGet package `ThemeForge.Theme`. The old `Services/ThemeService.cs` and legacy `*Theme.xaml` palettes were removed.

---

## Multi-Exec - Broadcast Sends to Wrong Terminals

**Symptom**: Broadcast keystrokes appear in terminals that should not be receiving them, or do not appear in terminals that are opted in.

**Root Cause**: The broadcast subscription list is keyed by session ID. If a session tab is closed and a new one is opened, the old session ID may linger in the broadcast list, or the new session may not be registered.

**Solution**:
1. Verify each terminal's broadcast toggle state in the session toolbar (broadcast icon should be highlighted when active)
2. On session close, the `EmbeddedSessionManager` must remove the session from the broadcast subscriber list
3. On session open, broadcast opt-in defaults to OFF - the user must explicitly enable it
4. Check that `PostWebMessageAsString` targets the correct `WebView2` instance by session ID, not by tab index

**Files**: `EmbeddedSessionManager.cs`, `EmbeddedSshView.xaml.cs`

---

## Quick Connect - Ad-Hoc SSH Fails

**Symptom**: Quick Connect (Ctrl+K) opens the overlay and parses the connection string, but the SSH connection fails with authentication errors or "no suitable method found".

**Root Cause**: Ad-hoc connections via Quick Connect create a transient `ServerProfileDto` without saved credentials. If the target server requires key-based authentication and no default key is configured, the connection has no viable auth method.

**Solution**:
1. Ensure Pageant is running with the appropriate keys loaded (Quick Connect uses Pageant as the default auth method for SSH)
2. Alternatively, specify credentials in the connection string or configure a default SSH key in Settings > Authentication
3. For password-based auth, the Quick Connect parser supports `user:password@host` format (credentials are not persisted)
4. Check that `AuthPreflightChecker` has valid auth sources before the connection attempt

**Files**: `ConnectionService.Ssh.cs`, `QuickConnectOverlay.xaml.cs`

---

## RDP Resize - Still Reconnecting (Delta/Debounce Tuning)

**Symptom**: Resizing the application window causes the embedded RDP session to briefly disconnect and reconnect, producing a visible flicker or momentary black screen.

**Root Cause**: The `UpdateResolution()` call triggers an RDP reconnect when the resolution delta exceeds the threshold. If the debounce timer is too short or the delta threshold is too low, rapid window resizing causes repeated reconnects.

**Solution**:
1. Increase the resize debounce interval (default: 500ms). A value of 800-1000ms reduces reconnects during active resizing
2. Increase the minimum resolution delta threshold - small changes (under 50px in either dimension) should be skipped entirely
3. Ensure `_allowResolutionUpdates` is still gated by the 5-second post-connect stabilization guard
4. If the issue persists, check that `OnResizeTimerTick` compares against the LAST APPLIED resolution, not the last requested one

**Files**: `EmbeddedRdpView.xaml.cs` - `OnResizeTimerTick()`, `UpdateResolution()`

---

## 27. VNC - noVNC Library Unavailable {#vnc-novnc-unavailable}

**Symptom**: VNC tab shows "noVNC Library Unavailable" error instead of the remote desktop.

**Root Cause**: The noVNC JavaScript library is loaded from CDN (`cdn.jsdelivr.net`). In offline or network-restricted environments, the import fails.

**Solution**:
1. Ensure the machine has internet connectivity
2. For air-gapped environments, download noVNC from `https://github.com/novnc/noVNC/releases` and place files in `Assets/vnc/`. Update `vnc.html` to import from the local path instead of CDN
3. The app shows a clear error message with instructions when the CDN is unreachable

**Files**: `Assets/vnc.html`, `Views/EmbeddedVncView.xaml.cs`

---

## 28. VNC - WebSocket Proxy Port Conflict {#vnc-websocket-port-conflict}

**Symptom**: VNC connection fails with a port binding error.

**Root Cause**: The `WebSocketVncProxy` binds to a random local port. In rare cases, the port may already be in use.

**Solution**: Retry the connection - a new random port will be selected. If the issue persists, check for processes hogging ephemeral ports.

**Files**: `Services/WebSocketVncProxy.cs`

---

## 29. X11 Forwarding - No Display {#x11-no-display}

**Symptom**: X11 forwarded applications fail with "Cannot open display" or similar errors.

**Root Cause**: No X11 server (VcXsrv, Xming, X410) is installed or running on the Windows host.

**Solution**:
1. Install VcXsrv from `https://sourceforge.net/projects/vcxsrv/` or Xming
2. Heimdall auto-detects and auto-starts the X server when X11 forwarding is enabled
3. If auto-start fails, set the X server path manually in Settings > X11 Server Path
4. Verify the `DISPLAY` environment variable is set (Heimdall sets `localhost:0.0` automatically)

**Files**: `Services/X11ServerManager.cs`, `Services/ConnectionService.Ssh.cs`

---

## 30. Telnet - Connection Hangs {#telnet-connection-hangs}

**Symptom**: Telnet connection appears to connect but no prompt appears.

**Root Cause**: Some Telnet servers require specific IAC negotiation responses. Heimdall's Telnet implementation handles basic DO/WILL/DONT/WONT negotiation but may not satisfy all server requirements.

**Solution**:
1. Verify the target port is correct (default: 23)
2. Test with a standard Telnet client to confirm the server works
3. Some legacy devices may require specific terminal type negotiation not yet implemented

**Files**: `Terminal/TelnetSession.cs`

---

## 31. FTP - Passive Mode Failures {#ftp-passive-mode}

**Symptom**: FTP directory listing works but file transfers fail or time out.

**Root Cause**: FTP uses separate data connections. Passive mode (default) requires the server to open a port that the client connects to. Firewalls may block these ports.

**Solution**:
1. Ensure the FTP server's passive port range is accessible
2. Heimdall uses FluentFTP `AutoPassive` when passive mode is enabled
3. Disable passive mode in the FTP profile when the server requires active mode; Heimdall then uses FluentFTP `AutoActive`

**Files**: `Sftp/FtpBrowser.cs`

---

## 32. Tab Detach - WebView2 Session Lost {#tab-detach-webview2}

**Symptom**: After detaching an SSH tab to a floating window and re-docking it, the terminal may lose its WebView2 state.

**Root Cause**: WebView2 controls can behave unpredictably when re-parented between WPF visual trees. The control maintains its internal state but the rendering context may need re-initialization.

**Solution**:
1. If the terminal appears blank after re-docking, the session is still alive - try clicking in the terminal area
2. For RDP sessions, detach is one-way (ActiveX controls cannot be safely re-parented)
3. Split sessions cannot be detached (by design)

**Files**: `Views/FloatingSessionWindow.xaml.cs`, `MainWindow.xaml.cs`

---

## 33. Ephemeral Server - Port 69 Access Denied {#tftp-port-access-denied}

Before troubleshooting connectivity, ensure TFTP is enabled in Settings > Advanced > File sharing. TFTP is opt-in since Phase 3.7 and the share runs HTTP-only by default.

**Symptom**: TFTP server fails to start with "access denied" on port 69.

**Root Cause**: Ports below 1024 require elevated privileges on Windows.

**Solution**:
1. Run Heimdall as Administrator if TFTP is needed
2. The HTTP server (port 8080) works without elevation
3. For non-elevated usage, TFTP is not available (this is a Windows security restriction)

**Files**: `Services/EphemeralFileServer.cs`

---

## 36. SFTP - Sudo Fallback Permission Denied (Auth Failure) {#sftp-sudo-fallback-auth-failure}

**Symptom**: SFTP operations on root-owned files show "Permission denied" even though sudo fallback should trigger. Log shows `SshAuthenticationException: Permission denied (publickey,password)`.

**Root cause**: The sudo helper methods (`DownloadViaSudoAsync`, `UploadViaSudoAsync`) were creating a raw `new SshClient(connInfo)` without Pageant/SSH agent integration or TOFU host key verification. The SSH connection itself failed before the sudo command could execute.

**Solution**:
1. Create a shared `CreateSudoSshClientAsync()` factory that uses `SshConnectionFactory.Create()` (same auth as main session: Pageant, keys, password)
2. Attach host key verification via `SshConnectionFactory.AttachHostKeyVerification()` using the stored `_hostKeyStore`
3. Store `_hostKeyStore` field in `EmbeddedSftpView` (was only passed to `RemoteFileEditor`)

**Key lesson**: Any secondary SSH connection (for sudo, health monitoring, etc.) MUST use the same factory and auth chain as the primary connection. Raw `SshClient` instances bypass Pageant, keyboard-interactive prompts, and TOFU verification.

**Files**: `Views/EmbeddedSftpView.xaml.cs`, `Ssh/SshConnectionFactory.cs`

---

## 37. SFTP - Sudo ls Parser Shows Empty Directory {#sftp-sudo-ls-parser-empty}

**Symptom**: Enabling "Browse as root" (sudo mode) shows an empty or nearly-empty directory listing. Only symlinks (like `/bin -> usr/bin`) appear.

**Root cause**: `ls -la --time-style=long-iso` produces **8 columns** per line:
```
drwxr-xr-x 2 root root 4096 2026-03-18 14:30 dirname
```
The parser used `Split(null, 9)` and checked `parts.Length < 9`, which skipped ALL entries with simple filenames (they only produced 8 tokens). Symlinks like `bin -> usr/bin` produced enough tokens to pass.

**Solution**: Changed to `Split(null, 8)` so the filename (which may contain spaces) stays intact as `parts[7]`. Check `parts.Length < 8`.

**Key lesson**: Always verify the actual column count of command output before writing a parser. Test with real server output, not assumptions.

**Files**: `Views/EmbeddedSftpView.xaml.cs` (`ParseLsOutput` method)

---

## 38. SFTP - Sudo Fallback Uses Typed Permission Denied Only {#sftp-typed-permission-denied-only}

**Symptom**: Uploading/downloading a root-owned file shows a generic transfer error instead of triggering sudo fallback. The log may show `SshException: Failure`.

**Root cause**: Heimdall intentionally no longer treats generic `Failure` strings as permission denied. That heuristic could trigger privileged sudo operations on non-permission failures such as channel errors, network drops, or server-side policy failures.

**Solution**: `EmbeddedSftpViewModel.IsPermissionDenied()` accepts typed permission-denied signals only: `SftpPermissionDeniedException` and local `UnauthorizedAccessException`. If a server returns an ambiguous generic failure for a real permission issue, retry manually with "Browse as root" or inspect the server logs; do not reintroduce substring matching.

**Key lesson**: False negatives are safer than privileged false positives. Sudo escalation must be based on typed errors, not message text.

**Files**: `Views/EmbeddedSftpView.xaml.cs` (`IsPermissionDenied` method)

---

## 39. WebView2 - Side-by-Side Configuration Error (0x800736B1) {#webview2-sxs-error}

**Symptom**: Embedded SSH terminal shows "WebView2 initialization failed: The application has failed to start because its side-by-side configuration is incorrect (0x800736B1)".

**Root cause**: The bundled WebView2 Fixed Version Runtime was an incomplete subset of files (cherry-picked DLLs). The `msedgewebview2.exe` manifest references specific VC++ runtime versions and SxS assemblies that must be present in the same directory.

**Solution**: Copy the FULL runtime directory from `C:\Program Files (x86)\Microsoft\EdgeWebView\Application\{version}\` instead of cherry-picking files. Trim only non-essential Edge bloat (Copilot, identity, extensions) but keep manifests, EBWebView, and all DLLs.

**Key lesson**: WebView2 Fixed Version Runtime is not a simple collection of DLLs. It requires SxS manifests and a specific directory structure. Always copy the complete runtime and trim conservatively.

**Files**: `Services/WebView2Helper.cs`, `Build.ps1`, `runtimes/webview2/`

---

## 40. HTTP Traversal - Sibling Prefix Bypass {#http-traversal-sibling-prefix}

**Symptom**: Security audit found that the EphemeralFileServer `StartsWith` check could be bypassed with sibling directory names. E.g., serving `/data` would also allow access to `/data-other/secret.txt`.

**Root cause**: `fullPath.StartsWith(_servingDirectory)` matches any path that starts with the same prefix, including sibling directories with similar names.

**Solution**: Append a trailing `Path.DirectorySeparatorChar` to the comparison base, and add an exact-root match fallback:
```csharp
var safeBase = _servingDirectory.EndsWith(Path.DirectorySeparatorChar)
    ? _servingDirectory
    : _servingDirectory + Path.DirectorySeparatorChar;
if (!fullPath.StartsWith(safeBase) && !string.Equals(fullPath, _servingDirectory))
```

Applied to both HTTP and TFTP handlers.

**Files**: `Services/EphemeralFileServer.cs`

## 41. Tool Tunnel Scan - Few or No Hosts Found {#tool-tunnel-scan-few-hosts}

**Symptom**: Network Cartography (or Port Scanner, Banner Grabber, Firewall Tester, Default Credential Scanner) via "Route via" SSH gateway finds only the gateway host or very few hosts, while a direct scan from the same subnet returns dozens.

**Root cause** (two issues):

1. **No per-probe timeout on `/dev/tcp`**: The bash built-in `echo >/dev/tcp/HOST/PORT` blocks for the kernel's full TCP retransmit timeout (20-127 seconds) on filtered ports (packets silently dropped by a firewall). With a 10-35 second `CommandTimeout` on the SSH channel, a single filtered port caused the entire scan command to be killed before reaching later ports.

2. **No host discovery phase** (Network Cartography only): The tunnel scan went straight to port probing without ping sweep or ARP table lookup. Only hosts with open ports on the exact scanned list were returned - hosts responding to ICMP but with no matching open ports were invisible.

Additionally, `/dev/tcp` is a bash-only feature. If the gateway's login shell is `dash` or `sh`, all probes silently fail.

**Solution** (applied):

- **Per-probe timeout**: All 5 tool views now use `timeout 2 bash -c "echo >/dev/tcp/HOST/PORT"` instead of bare `(echo >/dev/tcp/HOST/PORT)`. The `timeout` command sends SIGTERM after 2 seconds (clean process cleanup), and explicit `bash -c` ensures `/dev/tcp` support.

- **Network Cartography 3-phase tunnel scan**: (1) batch ping sweep via parallel background jobs + ARP table read, (2) batch reverse DNS in a single SSH command, (3) parallel `/dev/tcp` probes per host bounded by `sleep 5; kill $(jobs -p); wait`.

**Files**: `Views/Tools/NetworkCartographyView.xaml.cs`, `Views/Tools/PortScannerView.xaml.cs`, `Views/Tools/BannerGrabberView.xaml.cs`, `Views/Tools/FirewallTesterView.xaml.cs`, `Views/Tools/DefaultCredentialView.xaml.cs`

---

## 42. SSH - TestEnv Gateway Dropdown Empty After Import {#ssh-testenv-gateway-dropdown-empty}

**Symptom**: After importing `Heimdall-TestEnv` sessions, editing a tunneled SSH/SFTP/RDP session shows an empty gateway dropdown even though the imported profile has an `SshGatewayId`.

**Root cause**: `servers.testenv.json` contains server profiles, not `SshGatewayDto` entries. Heimdall stores SSH gateways in the runtime build's `config\settings.json` under `AppSettings.SshGateways`, while server profiles in `servers.json` only hold references to those gateway ids. If the active build configuration has no matching gateway in `settings.json`, the edit dialog has nothing to list and gateway-chain resolution cannot bind the referenced id.

This is easy to miss because the repo root also has a `config\settings.json`, but a Debug launch reads from `src\Heimdall.App\bin\Debug\net10.0-windows\config\settings.json`.

**Solution**: Inject the TestEnv gateway into the exact runtime settings file used by the executable, then restart Heimdall:

```powershell
& 'G:\_Projects\Tests\Heimdall-TestEnv\scripts\Inject-Gateway.ps1' `
  -SettingsPath 'G:\_dev\SnapConnect\Heimdall\src\Heimdall.App\bin\Debug\net10.0-windows\config\settings.json'
```

Alternatively, create the gateway manually in Settings > SSH & SFTP > SSH gateways and save settings. External edits to `settings.json` are not live-reloaded; restart the app after running the script.

**Files**: `G:\_Projects\Tests\Heimdall-TestEnv\scripts\Inject-Gateway.ps1`, runtime `config\settings.json`

---

## 43. RDP - `IMsRdpExtendedSettings` Not Reachable via `dynamic` Property Access {#rdp-extendedsettings-dynamic-access}

**Symptom**: Code accessing `ax.ExtendedSettings` where `ax` is the AxHost-wrapped OCX throws a runtime binder error similar to:

```text
RuntimeBinderException: 'System.__ComObject' does not contain a definition for 'ExtendedSettings'
```

This can happen even on Windows 10/11 machines where `MsTscAx.MsTscAx.10` is registered.

**Root cause**: The AxHost wrapper's `System.__ComObject` IDispatch surface does not expose the `ExtendedSettings` property, even though the underlying RDP OCX supports `IMsRdpClient10` / `IMsRdpExtendedSettings` through vtable QueryInterface.

**Solution**:

1. Declare a typed interop interface:

```csharp
[ComImport]
[Guid("302D8188-0052-4807-806A-362B628F9AC5")]
internal interface IMsRdpExtendedSettings
{
    void put_Property(string name, object value);
}
```

2. Get the real OCX object from `AxHost.GetOcx()`.
3. Use direct QI via a CLR COM cast:

```csharp
var extendedSettings = ocx as IMsRdpExtendedSettings;
```

4. Keep an explicit `Marshal.QueryInterface` fallback with the same IID for diagnostics on unusual COM environments.

Do **not** use `IServiceProvider.QueryService` for this case. On `MsTscAx.MsTscAx.10`, it returns `E_NOINTERFACE` for sibling COM interfaces and is the wrong acquisition pattern.

**Key lesson**: For MsTscAx sibling COM interfaces, trust direct QueryInterface on the OCX, not the dynamic IDispatch surface exposed by the AxHost wrapper.

**Files**: `Heimdall.Rdp/ActiveX/ComInterfaces.cs`, `Heimdall.Rdp/ActiveX/RdpActiveXHost.cs`

---

## 44. SSH - Host Key Unavailable on Plink Fallback {#ssh-host-key-unavailable-plink}

**Symptom**: A Pageant-only SSH or tunneled session fails before launching plink with a localized host-key-unavailable message.

**Root cause**: Heimdall could not resolve a host-key fingerprint through its own trust model. This can happen when there is no stored fingerprint and the `IPlinkHostKeyProbe` cannot parse a presented key, times out, or is unavailable. Heimdall deliberately refuses to fall back to PuTTY/Plink's registry cache because that would bypass the app's TOFU store.

**Solution**:

1. Start a normal interactive SSH/SFTP connection path that can complete the Heimdall host-key prompt, or import the key through `Settings > SSH & SFTP > Trusted host keys`.
2. Verify that the configured plink path is valid and that `PlinkHostKeyProbe` can execute it.
3. For tests, inject `IPlinkHostKeyProbe`; do not spawn `plink.exe` from unit tests.

**Key lesson**: `SshFailureCode.HostKeyUnavailable` means no trustworthy comparison could be made. It is not a mismatch; do not remap it to `HostKeyMismatch`.

**Files**: `Services/PlinkHostKeyDecider.cs`, `Services/IPlinkHostKeyProbe.cs`, `Services/TunnelService.cs`, `Services/Handlers/SshHandler.cs`

---

## 45. SSH/SFTP - Host Key Mismatch Mid-Session {#ssh-sftp-host-key-mismatch-mid-session}

**Symptom**: An existing SSH/SFTP session disconnects and the UI shows a security warning instead of a generic reconnect prompt. SSH auto-reconnect does not start.

**Root cause**: A secondary connection or read-loop path observed `HostKeyRejectedException` after the session was already established. Heimdall routes this through typed security events so a possible MITM is not hidden behind "Disconnected".

**Solution**:

1. Treat the warning as a security event. Compare the presented fingerprint with an out-of-band trusted source.
2. If the host was legitimately re-keyed, update the trusted host key through the explicit trust UI.
3. Do not auto-reconnect or silently accept from this path.

**Key lesson**: `SshSessionSecurityEvent` and `HostKeyRotatedDuringUpload` are additive signals. Existing `Disconnected` events still fire for compatibility, but security UI must preserve the typed event.

**Files**: `SshSessionFailureDispatcher.cs`, `SshSessionSecurityEvent.cs`, `SftpBrowser.cs`, `SshShellSession.cs`, `RemoteFileEditor.cs`, `Views/EmbeddedSftpView.xaml.cs`, `Views/EmbeddedSshView.xaml.cs`

---

## 46. FTP - Cleartext Credential Warning {#ftp-cleartext-credential-warning}

**Symptom**: Connecting to FTP succeeds but the status area warns that the channel is cleartext.

**Root cause**: The profile uses FTP with `FtpUseSsl=false` and a non-empty username. In this mode, FTP transmits credentials without encryption.

**Solution**:

1. Prefer SFTP when the server supports SSH.
2. If FTP is required, enable FTPS (`FtpUseSsl=true`) and verify the server certificate path.
3. Leave anonymous cleartext FTP for public, non-sensitive endpoints only.

**Key lesson**: This is a non-blocking `ConnectionResult.Warning`, not a connection failure. It should route to status text rather than a modal so legacy FTP workflows remain possible.

**Files**: `Services/Handlers/FtpHandler.cs`, `Services/ProtocolSessionResults.cs`, `Services/ConnectionService.cs`, `Sftp/FtpBrowser.cs`

---

## 47. WinRM Gateway - HTTP 12152 Invalid Server Response {#winrm-gateway-12152}

**Symptom**: A WinRM profile routed through an SSH gateway fails with WinHTTP error `12152` ("the server returned an invalid or unrecognized response"). The Heimdall log shows the tunnel established cleanly; the error surfaces inside the PowerShell terminal.

**Root cause**: Environmental, not a Heimdall fault. A hand-built `plink` tunnel reproduces the failure outside Heimdall - the TCP forward opens (`TcpTestSucceeded: True`) but the HTTP/WinRM exchange is closed by the target service or an application-layer device on the `bastion → target` path. An RDP profile through the same bastion works, confirming the tunnel machinery is sound.

**Solution**: No code fix in Heimdall. Diagnose the environment path (bastion forwarding policy, target WinRM listener, intermediate IPS/proxy). Full isolation procedure and `plink` console reading grid: see [winrm-gateway-12152-diagnostic.md](winrm-gateway-12152-diagnostic.md).

**Files**: none (environmental).

---

## 48. KeePassXC Credential Provider - Common Gotchas {#keepassxc-credential-provider}

**Symptom**: The external credential provider returns nothing, reports "not found", or the Settings Test button fails when using KeePassXC.

**Root cause**: Almost always a configuration mismatch rather than a bug. The usual suspects:

- **`keepassxc-cli` is not installed or not found.** It must be on `PATH`, or installed at `C:\Program Files\KeePassXC\` (the bundle ships `keepassxc-cli.exe`). Install KeePassXC if it is missing.
- **The vault entry title does not match.** The lookup uses the per-profile "Vault entry name" and falls back to the server display name. The KeePassXC entry **title** must match that value exactly. This is the number one cause of "not found".
- **Wrong preset for the database type.** Pick the preset that matches the database: "KeePassXC (key file)" for password + key file, "KeePassXC (key file only)" for a key-file-only database (`--no-password`), or "KeePassXC" for a master-password-only database.
- **Unlock secret confusion.** The Unlock secret field is the database **master password**, fed to the tool over stdin. Leave it empty for key-file-only databases (the preset uses `--no-password` and never reads stdin).

**Solution**:

1. Confirm `keepassxc-cli --version` runs (PATH or the full install path).
2. Make the vault entry title equal the profile's "Vault entry name" (or display name).
3. Select the preset matching the database, and set the Key file path when the preset includes `{KeyFile}`.
4. Set the Unlock secret to the master password, or leave it empty for key-file-only databases.
5. Use the Settings Test button to confirm retrieval before connecting.

**Key lesson**: KeePassXC itself does not need to be running or unlocked - `keepassxc-cli` opens the `.kdbx` file directly using the master password and/or key file supplied by Heimdall.

**Files**: `Security/CommandCredentialProvider.cs`, `Security/CredentialProviderFactory.cs`, `Services/CredentialProviderPresetService.cs`, `ViewModels/SettingsViewModel.cs`

---

## 49. RDP Embedded - Session Cut Off Shortly After Connecting {#rdp-slow-server-cutoff}

**Symptom**: A server whose Windows session loads slower than usual is dropped by Heimdall shortly after it has already reported "Connected". The session is visibly up first, then cut on its own.

**Root cause**: Unconfirmed. Three candidates remain: a forced resize about ten seconds after connect, an exhausted auto-reconnect, or a premature credential cleanup. No log of a slow reproduction exists yet, which is why none of them has been ruled out.

**Solution**: None yet. What settles it is one clean capture covering the whole sequence, with the cutoff time noted by hand: see [repro/capture-rdp-slow-server-cutoff-log.md](repro/capture-rdp-slow-server-cutoff-log.md). The procedure is written for whoever can reproduce the failure, and one capture following it is enough to tell the three candidates apart.

**Files**: none until the cause is confirmed.

## 50. Scheduled Task - Ran But Connected Nothing {#scheduled-task-connected-nothing}

**Symptom**: The Scheduled tab shows a recent run for a task, no session was opened, and the log holds a line beginning with `Scheduled task '<name>' (serverId=<id>) connected nothing:`.

**Root cause**: The task matched no profile. The rest of the line says which of three cases it was: no profile carries the identifier the task records (the profile was deleted or re-created, and a profile with the same name is never assumed to be its replacement); the task records no identifier and no profile carries its name; or the task records no identifier and several profiles carry its name, so which one it means cannot be established. The last-run time is stamped before the task runs, which is why the tab shows a run.

**Solution**: Edit the task and point it at the profile you mean. A task saved from the current version records that profile's identifier and no longer depends on its name.

**Files**: `src/Heimdall.App/ViewModels/Scheduled/ScheduledTaskServerResolver.cs`, `src/Heimdall.App/ViewModels/Scheduled/ScheduledTasksViewModel.cs`
