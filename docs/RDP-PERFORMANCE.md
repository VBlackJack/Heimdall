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
actually change that. Every number here was measured, on a Windows Server 2022 target, in
August 2026. Where a setting does nothing, this page says so rather than repeating advice
that sounds plausible.

## What a session costs

| Item | Private commit |
|---|---:|
| Heimdall running, no session open | about 194 MB |
| First RDP session: one-time control initialisation | +68 MB |
| Each session, including the first | +194 MB |

So a single session lands around 456 MB, two around 650 MB, three around 844 MB. Those last
two are extrapolated from a marginal cost measured once, not measured directly.

At 1920x1080 a session costs roughly 80 MB more than at a small window size.

**Most of that memory is not Heimdall's.** It belongs to `MsTscAx`, the Microsoft RDP ActiveX
control, which Heimdall hosts in its own process. The same control is what `mstsc.exe` uses.

## Heimdall against the native client

Measured on the same target, same profile, with `mstsc.exe` launched by Heimdall in external
mode:

| Sessions | Heimdall | N separate mstsc.exe |
|---:|---:|---:|
| 1 | 456 MB | **328 MB** |
| 2 | 650 MB | 656 MB |
| 3 | **844 MB** | 983 MB |

One session is cheaper in the native client, because Heimdall carries its own application
baseline. From three sessions onward Heimdall is cheaper, because every separate `mstsc.exe`
re-pays its own process baseline while Heimdall amortises it across tabs. The crossover sits
at two sessions.

If you routinely keep one session open, the native client uses less memory. If you keep
several, Heimdall uses less.

## The one setting that works

**Resolution.** It is the only setting measured to move the number. Going from 1920x1080 to a
smaller session saved about 86 MB.

In a server profile, under the RDP tab, set **Resolution mode** to `Fixed` and choose a size
smaller than your monitor, or leave it on `Auto` and make the Heimdall window smaller. Both
reduce the negotiated session geometry.

This is a real trade: a smaller session is a smaller remote desktop to work in. Nothing here
gives you memory for free.

## The settings that do not work

**Colour depth.** Dropping from 32-bit to 16-bit saved nothing measurable. The difference fell
below the run-to-run noise of the measurement, and the sign was unfavourable. Lower it if you
are short of bandwidth, not if you are short of memory.

**Keep bitmap cache on disk.** This checkbox was called "Bitmap caching" until the name was
found to mislead. It controls whether the bitmap cache is written **to disk** between sessions.
It does not control the in-memory cache, and there is no setting in Heimdall that does. Turning
it off frees no memory, and it costs you the disk cache that would otherwise spare some redraws
on reconnect.

**Compression.** This is a bandwidth setting. It was not measured to affect memory and there
is no mechanism by which it would.

## Reading a memory figure without fooling yourself

Three traps cost us real time while producing the numbers above. They will cost you the same
if you check our work.

**Task Manager's "Memory" column is not a stable quantity.** Windows trims the working set of
a window that is not in the foreground. We watched the same unchanged process report 104 MB
and then 38 MB, while the memory it actually held never moved. Compare private commit instead,
visible in Task Manager's Details tab as "Commit size".

**Task Manager sums child processes into the application row.** Heimdall spawns WebView2
processes for terminal and file browser panes. A figure read from the grouped row is the whole
tree, not the application.

**A connected session is not a logged-in session.** A client sitting on a login screen costs a
fraction of a real desktop. If you compare two measurements, make sure both are in the same
state.

## Measuring it yourself

The harness used for this page ships in the repository at
`local/scripts/Measure-RdpMemory.ps1`. It samples private commit, working set, handles and
threads, counts established RDP connections so plateaus segment themselves, and records
whether the window was in the foreground so trimmed samples stay identifiable.

```powershell
pwsh -File local/scripts/Measure-RdpMemory.ps1 -ProcessName Heimdall -DurationMinutes 20
```

Anchor conclusions on the **delta** between plateaus, never on the absolute baseline. Across
identical launches we measured baselines from 191 MB to 214 MB, a 23 MB spread, while the
delta between plateaus reproduced to within 3 MB.

## Related

- [Settings FAQ](SETTINGS-FAQ.md) - what each option in Settings does.
- [Troubleshooting](TROUBLESHOOTING.md) - specific failures.
