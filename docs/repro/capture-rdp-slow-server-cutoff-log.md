## 🟦 Capture a Diagnostic Log for the Slow-Server RDP Cutoff

*Also available in French: [../fr/repro/capture-rdp-slow-server-cutoff-log.md](../fr/repro/capture-rdp-slow-server-cutoff-log.md).*

> Some RDP servers whose Windows session loads slower than usual get their
> connection dropped by Heimdall **after** it has already reported "Connected".
> No log of a slow reproduction exists yet, so the cause is still unconfirmed - 
> the three candidates are a forced resize ~10 s after connect, an exhausted
> auto-reconnect, or a premature credential cleanup. A single clean
> `FileLogger` capture that covers the **whole** sequence, with the **exact
> cutoff time** noted by hand, is enough to tell them apart. This procedure
> makes sure one capture is enough.
>
> ⚠️ The exact wording of the logging toggle in Settings may differ slightly
> by build - the steps below describe what to look for.

### 📋 What you need before starting

| Item | Detail |
|---|---|
| 🖥️ The affected machine | The PC where the slow-server RDP cutoff actually reproduces |
| 🧩 Heimdall installed | A working build that shows the bug |
| 📛 The slow server profile | The exact RDP server profile that triggers the premature cutoff |
| ⏱️ A visible clock | Phone or taskbar clock, to note times down to the second |
| 📝 Somewhere to write notes | A text file or paper for the context notes (Step 4) |

### ✅ STEP 1 - Turn on file logging

> Heimdall writes one log file per day, but only when logging is enabled in
> Settings. The whole diagnosis depends on that file, so this is locked ON
> before anything else.

| # | Action |
|---|---|
| ☐ 1 | Open **Heimdall** |
| ☐ 2 | Go to **Settings** |
| ☐ 3 | Find the logging toggle (labelled something like **Enable logging** / **Diagnostic logging**) → make sure it is **ON** |
| ☐ 4 | Save / close Settings so the change is applied |

### ✅ STEP 2 - Start from a clean log state

> All of today's events land in a single file. Starting Heimdall fresh, with
> logging already on, guarantees the log begins **before** the connection - and
> makes the reproduction easy to find in the file.

| # | Action |
|---|---|
| ☐ 1 | Close **Heimdall** completely |
| ☐ 2 | Open the log folder: it is the **`logs`** sub-folder next to the Heimdall executable (right-click the app shortcut → **Open file location** if unsure) |
| ☐ 3 | *(Optional)* Move today's existing **`heimdall_<date>.log`** file aside (a fresh one is created on next launch) |
| ☐ 4 | Re-launch **Heimdall** |
| ☐ 5 | Confirm a file named **`heimdall_<today's date>.log`** exists and ends with a recent **`Heimdall starting`** line |

### ✅ STEP 3 - Reproduce the cutoff and record the exact times

> We need the full sequence in the log - connecting, connected, then the
> premature cut. The hand-noted cutoff time is what lets us line the log up
> against the failure, so be precise about it.

| # | Action |
|---|---|
| ☐ 1 | ⚠️ Do not connect to any other server first - keep this run focused on the one attempt |
| ☐ 2 | Note the current wall-clock time (**HH:MM:SS**) - this is the "connect start" time |
| ☐ 3 | Open the slow RDP server profile and start the connection |
| ☐ 4 | Write down the connection mode used: **Embedded** (session inside a Heimdall tab) or **External** (separate mstsc window) |
| ☐ 5 | ⚠️ Do not touch, resize, click or move the session - just watch it |
| ☐ 6 | Wait for the premature cutoff to happen on its own |
| ☐ 7 | The instant the session is cut, note the **exact wall-clock time of the cut (HH:MM:SS)** |
| ☐ 8 | Copy, word for word, the on-screen message Heimdall shows when it disconnects |

### ✅ STEP 4 - Collect the log and the context note

> The raw log alone is not enough - a few facts it cannot record turn a long
> diagnosis into a short one. Collect them while the reproduction is fresh.

| # | Action |
|---|---|
| ☐ 1 | Wait about **30 seconds** after the cut (let any retry/teardown finish writing to the file) |
| ☐ 2 | Close **Heimdall** normally |
| ☐ 3 | In the **`logs`** folder, take **`heimdall_<date>.log`** and copy it under a clear name, e.g. **`heimdall_rdp-slow-cutoff_<date>.log`** |
| ☐ 4 | Write a short note next to it with: server host, **Embedded/External** mode, connect start time, **cut time**, the on-screen disconnect message, and how long the session was visibly up before it cut |
| ☐ 5 | Bring both the renamed log file and the note back (this PC is not reachable from the assisted session) |

### ✅ STEP 5 - *(Optional)* Capture a second run

> If the cutoff is intermittent, one log might miss the trigger. A second clean
> capture removes the doubt.

| # | Action |
|---|---|
| ☐ 1 | Repeat **Steps 2 → 4** once more if you have time |
| ☐ 2 | Keep the two logs under distinct names so they are not confused |

### 🆘 Common issues

| Symptom | Quick fix |
|---|---|
| No `heimdall_<date>.log` file appears | Logging is still off - re-check the Settings toggle in Step 1, then re-launch |
| Log file exists but stops before the cut | The app was closed too quickly - wait ~30 s after the cut before closing |
| Can't find the `logs` folder | It sits next to the Heimdall executable - right-click the shortcut → **Open file location** |
| The server connected fine, no cutoff | The bug is intermittent - retry, and only keep logs where the cut actually happened |
| Not sure if it was Embedded or External | External opens a separate **mstsc** window; Embedded shows the session inside a Heimdall tab |
