<!--
  Copyright 2026 Julien Bombled

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

      http://www.apache.org/licenses/LICENSE-2.0
-->
# Heimdall - RDP memory and session tuning

*Also available in French: [fr/RDP-PERFORMANCE.md](fr/RDP-PERFORMANCE.md).*

This page answers one question: how much memory an RDP session costs, and which settings
actually change that. Every number here was measured, on Windows Server 2022 targets, in
August 2026. Where a setting does nothing, this page says so rather than repeating advice
that sounds plausible.

## What a session costs

Measured on 2026-08-24, three concurrent sessions at 1920x1080 in 32-bit colour, private
commit over the whole process tree, each plateau left to settle:

| State | Before v2026.082401 | From v2026.082401 |
|---|---:|---:|
| Heimdall running, no session open | about 197 MB | about 197 MB |
| Three sessions | **1146 MB** | **763 MB** |
| Windows handles, three sessions | 3898 | 3058 |

The change is one setting, described below. It is worth 383 MB and 840 handles across three
sessions, a third of the footprint.

**Most of that memory is not Heimdall's.** It belongs to `MsTscAx`, the Microsoft RDP ActiveX
control, which Heimdall hosts in its own process. The same control is what `mstsc.exe` uses.

## The setting that matters

**Hardware-accelerated rendering.** With it on, the control builds a Direct3D device and a
decoding context **for every session you open**. That is why memory grows with the number of
sessions rather than with the traffic in them.

It is **off by default** from v2026.082401. In Settings it sits on the **RDP** tab, under the
**Performance** sub-tab. Per server it sits on that server's **Options** tab, in the
**RDP session options** card, under the same **Performance** sub-tab.

| Three sessions | Private commit | Handles |
|---|---:|---:|
| hardware rendering on | 1145.9 MB | 3898 |
| **hardware rendering off** | **763.3 MB** | **3058** |

**The trade, stated plainly.** Turning it off moves decoding from the graphics card to the
processor. On idle desktops and on scrolling text no difference was measurable, both under half
a percent of one core. **A session showing video or continuous animation was not measured**, for
want of a way to drive a repeatable moving picture into a test session. If a session feels less
smooth than it used to, switch the setting back on for that server.

## The other setting that works

**Resolution.** Going from 1920x1080 to a smaller session saved about 86 MB per session.

In a server profile, open the **Options** tab and, in the **RDP session options** card, the
**Display & Audio** sub-tab. Under **Resolution profile**, expand **More display options**, set
**Resolution mode** to `Fixed` and choose a size smaller than your monitor. Or leave the profile
on `Auto` and make the Heimdall window smaller. Both reduce the negotiated session geometry.

This is a real trade: a smaller session is a smaller remote desktop to work in.

## The settings that do not work

**Colour depth.** Dropping from 32-bit to 16-bit saved nothing measurable.

**Keep bitmap cache on disk.** This checkbox was called "Bitmap caching" until the name was
found to mislead. It controls whether the bitmap cache is written **to disk** between sessions.
It does not control the in-memory cache. Turning it off frees no memory, and it costs you the
disk cache that would otherwise spare some redraws on reconnect.

**Image stretching (smart sizing).** Disabling it costs about 16 MB more across three sessions,
not less. The scaling happens in the window drawing layer, not in the control's buffers.

**Compression.** This is a bandwidth setting. It was not measured to affect memory and there is
no mechanism by which it would.

## Memory that is not returned when you close a tab, and for how long

Heimdall keeps up to two RDP controls alive after their tabs are closed, so the next connection
is fast and does not re-pay a leak inside `mstscax.dll` that costs about 66 kernel handles per
freshly created control. Each control kept alive holds roughly 300 MB.

Measured: closing all three sessions returned the process to 799 MB rather than to its 197 MB
baseline, and it stayed there 25 minutes later. **The amount is bounded, not a runaway**: a
second open-and-close cycle added 9.7 MB, not another 600.

Since v2026.090503 an idle control is released after **five minutes** by default, and the pool
is a pair of settings on the **RDP** tab, under the **Performance** sub-tab: **Idle Remote
Desktop controls kept for reuse** (0 to 8; 0 creates a control per session, as before pooling
existed) and **Idle control expiry** in minutes (0 keeps them until Heimdall exits, which is
what every earlier version did). Both apply without a restart: the next tab you close and the
next expiry check read the new values. The idle controls are also released when Heimdall exits.

So a Heimdall that has been used still sits higher than one just started, but only for as long
as the expiry says, and the expiry is yours to set.

## Against other clients

Against `mstsc.exe`, launched by Heimdall in external mode on the same target: one session is
cheaper in the native client, because Heimdall carries its own application baseline; from three
sessions onward Heimdall is cheaper, because every separate `mstsc.exe` re-pays its own process
baseline while Heimdall amortises it across tabs. The crossover sits at two sessions.

Against Devolutions Remote Desktop Manager Free 2026.2, measured on the same targets within the
same half hour, before the hardware-rendering change:

| Sessions | Heimdall | RDM |
|---:|---:|---:|
| 0 | 197 MB | 347 MB |
| 1 | 463 MB | 463 MB |
| 3 | 929 MB | 682 MB |

Both clients load the same `mstscax.dll`. The gap was entirely the hardware-rendering property,
which RDM disables on its default path and Heimdall used to leave at the control's default. That
comparison is what led to the change above.

## Reading a memory figure without fooling yourself

Four traps cost real time while producing the numbers on this page. They will cost you the same
if you check the work.

**What is on the remote desktop dominates everything else.** The same binary, the same protocol
and the same targets measured 929 MB at one point in the day and 1253 MB five hours later,
because Server Manager, Computer Management, a text editor and a console had been opened inside
the sessions meanwhile. More painted content, more memory. **Never compare two measurements
taken hours apart**; compare arms minutes apart on identical desktops, or you will diagnose a
code regression that does not exist.

**Task Manager's "Memory" column is not a stable quantity.** Windows trims the working set of a
window that is not in the foreground. The same unchanged process reported 104 MB and then 38 MB,
while the memory it actually held never moved. Compare private commit instead, visible in Task
Manager's Details tab as "Commit size". At three sessions the working set does not even separate
Heimdall from RDM (355 MB against 353 MB) while private commit differs by 248 MB.

**Task Manager sums child processes into the application row.** Heimdall spawns WebView2
processes for terminal and file browser panes. A figure read from the grouped row is the whole
tree, not the application.

**A connected session is not a logged-in session.** A client sitting on a login screen costs a
fraction of a real desktop. If you compare two measurements, make sure both are in the same
state.

## Measuring it yourself

No measurement harness is shipped with the repository. The numbers above came from sampling the
counters Windows already exposes, on a timer, and that is all it takes to reproduce them.

Sample the whole process family in one pass - Heimdall spawns WebView2 children for the terminal
and file browser panes - and, if you are comparing against another client, read both families on
the same tick. Record for every sample: **private commit**, which is the figure every table above
uses; working set, kept only to show when Windows has trimmed it; handle and thread counts; the
number of established connections to the RDP port, so plateaus segment themselves; and whether
the window was in the foreground, so trimmed samples stay identifiable.

```powershell
$family = Get-Process -Name Heimdall, msedgewebview2 -ErrorAction SilentlyContinue
[math]::Round(($family | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB, 1)
($family | Measure-Object HandleCount -Sum).Sum
(Get-NetTCPConnection -RemotePort 3389 -State Established).Count
```

Anchor conclusions on the **delta** between plateaus, never on the absolute baseline. Across
identical launches baselines from 189 MB to 214 MB were measured, a 25 MB spread, while the
delta between settled plateaus reproduced to within 3 MB.

Let each plateau settle. Values read after a hundred seconds moved by more than a hundred
megabytes against the same plateau read after four minutes, which was enough to invert one
conclusion before it was caught.

## Related

- [Settings FAQ](SETTINGS-FAQ.md) - what each option in Settings does.
- [Troubleshooting](TROUBLESHOOTING.md) - specific failures.
