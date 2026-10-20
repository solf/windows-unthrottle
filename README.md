# unthrottle

Stops Windows from squeezing your busy background processes onto the efficiency cores.

On Intel hybrid CPUs (12th gen and later: P-cores + E-cores), Windows throttles processes
whose window is not in the foreground. A build or test run that you start in a console and
then leave behind another window can end up confined to the E-cores, with the P-cores sitting
idle. `unthrottle` watches the processes you name, and when one of them is busy it opts that
process out of throttling. When it goes idle again, it hands control back to Windows.

It is a single small executable. It needs no admin rights, no service and no installer.

## The problem, measured

The measurements are from an sbt build and test run on an i7-13620H (6 P-cores with 12 threads
= CPUs 0-11, 4 E-cores = CPUs 12-15) under Windows 10 21H2, sampled every few seconds:

| Console window | P-cores busy | E-cores busy | Threads ready but waiting | Same build step took |
|---|---|---|---|---|
| in the background | ~5-15% | **90-100%** | up to **92** | ~106 s |
| in the foreground | up to 100% | up to 100% | ~0 | - |
| background, pinned to P-cores | 40-60% | (excluded) | 0-4 | ~41 s |
| background, throttling flag cleared | 54-76% | 48-89% | 0 | - |

The 106 s run spent its first ~20 s in the foreground; the 41 s run was in the background throughout.

The change followed the window: bringing the console to the front spread the load over all
cores within one sample, and sending it back confined it to the E-cores again. Clearing the
process's throttling flag had the same effect while the window stayed in the background. A JVM
started afterwards, without the flag cleared, was confined again about 13 seconds after it started.

On Windows 10, Task Manager's "Power throttling" column showed *Disabled* for every process,
even while this was happening. Don't rely on it to diagnose the problem; look at the per-core
load instead.

## How it works

- Every `probe_ms` it samples the CPU time of each process whose exe name is in `processes`.
- When a process averages at least `unthrottle_cores` for `unthrottle_probes` probes in a row, it is
  **unthrottled**: explicitly opted out of execution-speed throttling. This uses `SetProcessInformation(ProcessPowerThrottling)`
  with `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` in the control mask and 0 in the state mask,
  the same per-process setting an application can set on itself.
- Once an unthrottled process stays below `reset_cores` for `reset_seconds`, it is **reset**: the
  override is removed and Windows decides again.
- It does not touch affinity or priority, so an unthrottled process can still use the E-cores as
  extra capacity.

It reacts to CPU load, not to which window is in front. A listed process that keeps a core busy
in the foreground gets unthrottled too, which is harmless because it was not being throttled there.

## Requirements

- Windows 10 1709 or later (the per-process throttling API). Built and tested only on
  Windows 10 21H2 with an i7-13620H. **Windows 11 has not been tried.** It has the same API
  and a more elaborate scheduler for hybrid CPUs, so the behaviour may differ.
- .NET Framework 4.x, which is part of Windows 10 and 11.
- It can only change processes of the user it runs as. Run it as your own user.

## Build

```
build.bat
```

This compiles `unthrottle.cs` with the C# compiler that ships with .NET Framework
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No SDK or Visual Studio is needed.

## Run

| | |
|---|---|
| Start | `unthrottle.exe`. No window appears. Only one instance runs per user; a second start shows a message box saying it is already running. |
| Stop | `unthrottle.exe stop`. Every process it unthrottled is reset first. If it is not running, a message box says so. |
| Start at logon | Put a shortcut to `unthrottle.exe` in your Startup folder (Win+R, then `shell:startup`). |

If `unthrottle.ini` is missing or invalid at startup, or the program crashes, it shows a message
box with the reason, writes it to `unthrottle.log`, and exits. Exit codes: 0 ok, 1 `stop` found
nothing running, 2 already running, 3 bad or missing config, 4 crash.

## Configuration

`unthrottle.ini` is read from the folder that contains `unthrottle.exe`, whatever the working
directory is, so a shortcut's "Start in" field does not matter. `unthrottle.log` is written to the
same folder.

Every key in the table below is required, and there are no built-in defaults: a missing key, an
unknown key (usually a typo) or an invalid value stops startup with a message box listing the problems.

On every probe the watcher checks the ini's last-modified time. When it has changed, the file is
re-read and the log gets a `CONFIG reloaded` line, so edits take effect within a second and need no
restart. A saved file with errors is not applied: the log gets an `ERROR config not reloaded` line
and the previous settings stay in effect. If the ini is deleted while the watcher runs, the log
says so once and the current settings stay in effect.

| Key | Shipped value | Meaning |
|---|---|---|
| `processes` | `java.exe,javaw.exe` | Exe names to watch, comma-separated. |
| `probe_ms` | `1000` | Sampling interval in milliseconds. |
| `unthrottle_cores` | `0.8` | CPU load (in cores) that counts as busy. 0.8 also catches one saturated thread, which measures a little under 1.0. |
| `unthrottle_probes` | `2` | Consecutive busy probes needed before the process is unthrottled. |
| `reset_cores` | `0.2` | CPU load below which an unthrottled process counts as idle. |
| `reset_seconds` | `10` | How long it must stay idle before it is reset. |
| `log_max_kb` | `1024` | `unthrottle.log` is renamed to `unthrottle.log.1` once it reaches this size. |

## Log

`unthrottle.log` is written next to the exe. It records events, not every probe. For example:

```
2026-10-20 22:48:13.351 START      pid=15748 user=me processes=java.exe,javaw.exe probe_ms=1000 unthrottle: 2 probes >= 0.8 cores, reset: 10s < 0.2 cores
2026-10-20 22:48:26.640 UNTHROTTLE java.exe pid=41676: busy (3.0 cores), exempt from background throttling. cmd: "C:\...\java.exe" ... -jar sbt-launch.jar test
2026-10-20 22:48:40.839 RESET      java.exe pid=41676: idle for 10s, back to Windows' default throttling (unthrottled 14s, peak 3.0 cores)
```

Other per-process lines are `EXIT` (the process ended while unthrottled) and `RESET` with
"unthrottle stopping" (on `unthrottle.exe stop`). If Windows rejects a call, the tag becomes
`UNTHROTTLE FAILED` or `RESET FAILED` with the error. Windows 10 cannot report a process's
throttling state back, so the log only shows that the call was accepted; to see the effect,
watch per-core load in Task Manager or Performance Monitor.

## Alternatives

- **Pin to the P-cores**: e.g. `start "" /affinity FFF cmd /c build.bat`, where the mask depends
  on your CPU. Child processes inherit it, so this works, but the process can never use the
  E-cores, even when that would help.
- **`powercfg /powerthrottling disable /path <exe>`**: Windows' own per-exe opt-out. It needs
  admin, works per full path (each JDK install is a separate entry), and applies all the time
  rather than only while the process is busy. We did not test whether it fixes the case above.
- **Process Lasso** can set default affinities per process, but that is a Pro feature that
  stops working when the trial ends.

## Limitations

- Detection is load-based. A process that is busy but unimportant is unthrottled too, for as long
  as it stays busy. Keep the `processes` list to things you actually wait on.
- A process is unthrottled only after `unthrottle_probes` x `probe_ms` of sustained load (about 2 s
  by default), so the first moments of a run are still throttled.
- Processes of other users are skipped silently.

## License

Apache License 2.0, see [LICENSE](LICENSE).
