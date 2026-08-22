# macOS port — permissions, toolchain, and what actually blocks this

Working notes for porting `ddinfo-tools` to macOS (arm64). Written 2026-08-21 against
commit `332d2d1`. Everything below was **verified by running it on this machine**, not
inferred — where something is unverified it says so explicitly.

This file exists because two of the three hard parts of this port are *permissions*
problems, not code problems, and permissions problems are the kind that waste a whole
evening if nobody wrote them down.

---

## TL;DR

| Thing | Status |
|---|---|
| .NET 10 SDK on macOS | ✅ Installed at `~/.dotnet` (10.0.400). **Not on `PATH`** — see below |
| `DevilDaggersInfo.Tools.Engine` builds on macOS | ✅ Green today, unmodified |
| `DevilDaggersInfo.Tools` builds on macOS | ❌ 4 errors, all from one missing `<Otherwise>` |
| Devil Daggers installed on this Mac | ✅ Native Steam build present |
| `task_for_pid` on the game | ✅ **Proven working under `sudo`** (see §3a) |
| Mac MainBlock layout matches Windows/Linux | ✅ Proven — `FormatVersion 1`, parsed correctly |
| macOS marker offset from devildaggers.info | ❌ Does not exist — but **no longer needed** (§4) |
| Game process name match | ❌ Broken on macOS — `"Devil Daggers"`, not `devildaggers` |

The editors, mod manager, and replay tools are a straightforward port and need **no
special permissions whatsoever**. Practice mode / live stats works, but only when the
tool is launched with `sudo`.

---

## 0. Which features need permissions, and which don't

Worth being precise about, because "this tool needs `sudo`" is false and would scare
people off five features that don't.

| Feature | What it touches | Needs `sudo`? |
|---|---|---|
| Spawnset / Survival editor | Files on disk | No |
| Asset editor | Files on disk | No |
| Replay editor | Files on disk | No |
| Mod manager | Renames files in the game's folder | No |
| Applying practice spawnsets | Writes a generated file to `mods/survival` | No |
| **Run Analysis (live splits/gems/homing)** | **Reads the running game's memory** | **Yes** |
| **Custom leaderboard recording** | **Reads the running game's memory** | **Yes** |
| **Replay inject / read from memory** | **Reads the running game's memory** | **Yes** |

Corrected 2026-08-22 after running the app: **"practice mode" is two separate things and
only one of them needs `sudo`.** Applying a practice spawnset is pure file I/O — it
generates a spawnset and writes it to `mods/survival`, exactly like the mod manager — and
works fine unelevated. It is only the *live* half, Run Analysis reading the running
process, that needs elevation. Saying "practice mode needs sudo" is wrong and scares
people off the half that works.

Only the memory-reading rows are affected by anything in §3.

### How mods work (they have nothing to do with permissions)

A mod is **just a file in a folder**. The game looks in its `mods` directory and loads
any file whose name begins with `audio` or `dd`. That is the entire mechanism.

So the mod manager's whole job is renaming files:

```csharp
// ModsDirectoryLogic.cs — disabling a mod
string newFileName = originalFileName.StartsWith("audio") || originalFileName.StartsWith("dd")
    ? $"_{originalFileName}"          // "dd"  → "_dd"  — game ignores it
    : originalFileName[1..];          // "_dd" → "dd"   — game loads it
File.Move(originalPath, newPath);
```

Enabled means the name starts with `audio`/`dd`; disabled means it has a leading
underscore. Nothing is injected into the game, nothing is patched, no process is touched.

**Launch the game from Steam exactly as normal.** The tool is a separate program that
runs alongside it — it is not a mod and does not change how the game starts.

## 1. Toolchain — the `PATH` trap

The .NET 10 SDK is installed at `~/.dotnet`, via Microsoft's official
`dotnet-install.sh`. No sudo, no system changes; delete the folder to uninstall.

**Bare `dotnet` does not work on this machine.** There is an older arm64 .NET host at
`/usr/local/share/dotnet` that wins on `PATH`, and it cannot see SDKs installed under
`~/.dotnet`. You get:

```
Requested SDK version: 10.0.100 — A compatible .NET SDK was not found.
```

...even though 10.0.400 is sitting right there. Always use the absolute path:

```bash
~/.dotnet/dotnet build src/DevilDaggersInfo.Tools/DevilDaggersInfo.Tools.csproj
```

This is the same class of bug as rule #1 in the global `CLAUDE.md` — a bare command name
resolving against an inherited `PATH`. It bites harder in unattended runs, where the
process inherits an even thinner environment than an interactive shell.

### The same trap bites again at runtime — `DOTNET_ROOT`

Verified 2026-08-21. Building is only half of it. The **compiled binary** asks the system
where .NET lives, is told `/usr/local/share/dotnet`, and never looks in `~/.dotnet`:

```
You must install or update .NET to run this application.
Architecture: arm64   Framework: 'Microsoft.NETCore.App', version '10.0.0' (arm64)
.NET location: /usr/local/share/dotnet
The following frameworks were found:  3.1.14 … 9.0.0
```

The 10.0.11 runtime is present the whole time, at
`~/.dotnet/shared/Microsoft.NETCore.App`. Set `DOTNET_ROOT` to point the app host at it:

```bash
DOTNET_ROOT="$HOME/.dotnet" ./src/artifacts/bin/DevilDaggersInfo.Tools/debug/ddinfo-tools
```

**`sudo` strips the environment**, so exporting it is not enough for practice mode — it
must be restated on the command itself:

```bash
sudo DOTNET_ROOT="$HOME/.dotnet" ./src/artifacts/bin/DevilDaggersInfo.Tools/debug/ddinfo-tools
```

And never `sudo dotnet run` — that invokes the SDK as root and leaves root-owned files in
`src/artifacts/` and `obj/`, which break every later ordinary build with permission errors
that look nothing like their cause. Build as your user, run the binary as root.

### macOS has no GL debug output

Verified 2026-08-21. With the runtime found, the app got as far as creating a window and a
GL context — confirming the forward-compatible hint works — and then died:

```
Silk.NET.Core.Loader.SymbolLoadingException: Native symbol not found (Symbol: glDebugMessageCallback)
   at DevilDaggersInfo.Tools.Container.GetGl(...) Container.cs:line 213
```

`glDebugMessageCallback` is OpenGL 4.3 / `KHR_debug`. macOS caps at 4.1 and never exposed
that extension, so the three `#if DEBUG` debug-output blocks in `Container.cs` are now
`#if DEBUG && !OSX`. Debug builds on macOS simply get no GL diagnostics; Windows and Linux
Debug builds are unaffected.

To fix the build-side lookup permanently for your shell (optional, does not help
unattended processes):

```bash
echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.zshrc
```

`global.json` pins `10.0.100` with `rollForward: latestMajor`, so 10.0.400 satisfies it.

---

## 2. Why the app doesn't compile on macOS

Not a porting problem — a hole in a preprocessor conditional.

`DevilDaggersInfo.Tools.csproj` has a `<Choose>` with exactly two `<When>` branches
(Windows, Linux) and **no `<Otherwise>`**. On macOS neither matches, so neither the
`WINDOWS` nor the `LINUX` symbol is defined, and `OutputType` is never assigned.

Then in `Container.cs`:

```csharp
private static GameMemoryService CreateGameMemoryService(ILogger logger)
{
#if WINDOWS
    return new GameMemoryService(new ...WindowsMemoryService());
#elif LINUX
    return new GameMemoryService(new ...LinuxMemoryService(logger));
#endif
}
```

With neither symbol defined the preprocessor deletes **both** returns, leaving a method
that promises a return value and has an empty body.

The complete failure list is four errors in two files:

| File | Line | Error |
|---|---|---|
| `Container.cs` | 343 | `CS0161` — `CreateGameMemoryService` not all paths return |
| `Container.cs` | 353 | `CS0161` — `CreateGameWindowService` not all paths return |
| `Container.cs` | 364 | `CS0161` — `CreatePlatformSpecificValues` not all paths return |
| `Ui/Config/ConfigLayout.cs` | 26 | `CS0103` — `examplePath` does not exist |

`ConfigLayout.cs` is the same cause: the `const string examplePath` lives inside the
`#if`, gets deleted, and the interpolated string below still references it.

**The fix is an `OSX` branch in the `.csproj` plus a third `#elif OSX` in each of those
four spots, and an `OSXValues : IPlatformSpecificValues`.** Roughly 30 lines. The
compiler tells you immediately whether you got it right — which is exactly why this is a
good project to run an automated loop against.

Note `Container.cs` also requests an OpenGL 3.3 Core context. macOS supports up to 4.1,
so the version is fine, but macOS **additionally requires the forward-compatible context
hint** — without it `glfwCreateWindow` fails outright with no useful error. Set
`GLFW_OPENGL_FORWARD_COMPAT` on macOS.

---

## 3. Permissions problem #1 — reading the game's memory

This is the one everybody expects.

Linux uses `process_vm_readv` / `process_vm_writev` against a pid
(`NativeInterface/Services/Linux/LinuxMemoryService.cs`, 137 lines total). macOS has no
equivalent syscall. You must go through Mach:

```
task_for_pid(mach_task_self(), pid, &task)   ← the gate
mach_vm_region(task, ...)                    ← enumerate mappings
mach_vm_read_overwrite(task, addr, len, ...) ← read
mach_vm_write(task, addr, ...)               ← write
```

`task_for_pid` on a process you don't own is privileged. There are exactly three ways
through it, and only the first two are realistic:

**(a) Run as root.** `sudo` the tool. Verified on this machine: without sudo,
`task_for_pid` returns `5` (`KERN_FAILURE`) even against *your own* shell. This is the
default macOS posture and it is not configurable away.

**(b) A code-signing entitlement.** `com.apple.security.cs.debugger`, on a binary signed
with a Developer ID certificate and hardened runtime. This requires a paid Apple
Developer account and Apple's approval of the entitlement request. It is **paperwork,
not code** — but it is paperwork you cannot skip if you ever want to hand this to the
Devil Daggers community as a double-clickable app.

**(c) Disabling SIP.** Do not. Not a real option, don't document it as one.

There is a further wrinkle worth knowing before you're surprised by it: even as root,
`task_for_pid` can be refused against a binary running under **hardened runtime** without
`get-task-allow`. Steam-distributed games are usually signed this way. The probe will tell
you which situation you're in — that's the whole point of running it.

### 3a. Probe result — verified 2026-08-21, it works

Run against the live game (pid 83466), mid-run, under `sudo`:

```
euid: 0
[1] OK — task_for_pid succeeded. Task port: 4611
[2] OK — 1757 regions seen, 1674 read, 14,784,851,968 bytes read
[3] OK — found '__ddstats__' at 2 addresses

    Marker '__ddstats__'   FormatVersion 1   PlayerId 379339
    PlayerName 'glorie_us'  Time 190.2310  Gems 83  Kills 274
```

Three things this settles permanently:

1. **macOS permits `task_for_pid` + `mach_vm_read_overwrite` under `sudo`.** No
   entitlement needed for local use. The entitlement only matters for shipping a signed
   `.app` to other people — already out of scope.
2. **The Mac build's `MainBlock` layout is identical to Windows and Linux.**
   `FormatVersion 1`, every field in the right place. `MainBlock.cs` needs no changes and
   no offsets need re-deriving.
3. **Scanning finds the block, so the server dependency in §4 is optional, not fatal.**
   Pointers to the block were located at `0x10224F868` and `0x102251E78`.

### Why Windows and Linux need none of this

The three platforms simply disagree about who may look inside a running program.

- **Windows** — reading another process's memory *as the same user* is an ordinary
  permission. `OpenProcess` with `PROCESS_VM_READ` succeeds with no elevation.
- **Linux** — `process_vm_readv` also works same-user, but many distributions ship the
  Yama `ptrace_scope` safety catch, which can refuse it. The Linux service already logs a
  specific hint about this on `EPERM`. So Linux isn't free either; the difference is that
  the user can change the setting themselves.
- **macOS** — Apple classifies reading another process's memory as *debugging*, and
  debugging is off by default, even for processes you own. This is deliberate: it's
  exactly the mechanism you'd use to lift passwords or session tokens out of a running
  app. There is no user-facing setting to relax it. Root or entitlement, nothing else.

**Do not "fix" this with a passwordless `sudoers` entry.** It edits your Mac's core
security configuration to save a password prompt, and a syntax error in that file can
lock you out of administrator access on your own machine. Bad trade. Use the
double-clickable launcher below instead.

### The double-clickable launcher

`~/Claude/Projects/ddmac-probe/Run Probe.command` — double-click it in Finder.

A `.command` file opens Terminal and runs itself, which is exactly what's needed here:
`sudo` must have a real terminal to prompt for a password. A normal `.app` double-click
has nowhere to ask, so it would fail silently — the same silent-failure trap described in
§3b below.

The launcher checks the game is running *before* asking for a password, explains why the
password is needed, runs the probe, and translates the exit code into a sentence.

### 3b. The process-name bug

Found while getting the probe to start, and it is a real defect in the port:

```csharp
// LinuxMemoryService.GetDevilDaggersProcess()
Array.Find(Process.GetProcesses(), p => p.ProcessName.StartsWith("devildaggers"));
```

On macOS the process is named **`Devil Daggers`** — capital letters and a space:

```
/Users/…/steamapps/common/devildaggers/Devil Daggers.app/Contents/MacOS/Devil Daggers
```

That match can never succeed, and it fails **silently**: `GetDevilDaggersProcess()`
returns `null`, `Initialize()` sets `IsInitialized = false`, and the UI simply shows no
game connected. No exception, no log line, nothing to grep for. `OSXMemoryService` must
normalise the name (lowercase and strip spaces before comparing).

### Running the probe manually

The probe lives **outside this repo** at `~/Claude/Projects/ddmac-probe` so it never
pollutes the fork. It is read-only: it never writes to the game and never writes a file.

```bash
~/.dotnet/dotnet build ~/Claude/Projects/ddmac-probe
```

Then launch Devil Daggers from Steam, **start an actual run** (the game may not populate
the stats block until you're playing), and:

```bash
sudo ~/.dotnet/dotnet run --project ~/Claude/Projects/ddmac-probe --no-build
```

Reading its output:

| Exit | Meaning | What to do |
|---|---|---|
| `0` | Everything works, MainBlock found and parsed | The risky assumption holds. Proceed. |
| `1` | Game process not found | Launch the game; or pass its pid as an argument |
| `2` | `task_for_pid` refused | If you weren't root, rerun with `sudo`. If you were, it's the entitlement wall |
| `3` | Got a task port, read nothing | Unexpected — record the region counts |
| `4` | Reads work, no `__ddstats__` marker | Retry mid-run. If still absent, the Mac build differs |

On success it also hunts for the 8-byte **pointer** to the block — see the next section
for why that number matters more than it looks.

---

## 4. Permissions problem #2 — the one nobody expects

**This is the actual blocker for practice mode, and it is not a permissions problem on
your machine at all. It is a dependency on someone else's server.**

The tool does **not** scan memory for the marker. Despite the marker string existing, it
is only ever parsed as a sanity check, never searched for. What actually happens
(`GameMemory/GameMemoryService.cs:35`):

```csharp
nativeMemoryService.ReadMemory(_process,
    _process.MainModule.BaseAddress.ToInt64() + ddstatsMarkerOffset,
    _pointerBuffer, 0, sizeof(long));
```

A **fixed offset from the module base**, holding a pointer to the real block. And that
offset is fetched over the network (`Networking/TaskHandlers/FetchMarker.cs`,
`Networking/ApiHttpClient.cs:89`):

```
GET https://devildaggers.info/api/app/process-memory/marker?appOperatingSystem={Windows|Linux}
```

The `AppOperatingSystem` enum ships in the external NuGet package
`DevilDaggersInfo.Web.ApiSpec.Tools 2.0.0`. Dumped from the assembly on this machine, it
has **exactly two members: `Windows` and `Linux`.** There is no macOS value, no macOS
route, and no macOS offset in that database.

So a complete, correct, beautifully written macOS memory service would still have nothing
to point at.

**Three ways out, in ascending order of effort:**

1. **Derive the offset locally and hardcode it behind `OSXValues`.** The probe already
   does the hard half — it finds the block by scanning and then locates the pointer to it
   (`0x10224F868`, `0x102251E78`). Fine for your own machine. Brittle: it breaks on every
   game update.
2. **Scan instead of fetch, on macOS only.** ← *proven working, 2026-08-21.* Search the
   address space for `__ddstats__` directly and skip the offset entirely. The probe did
   exactly this and parsed a correct `MainBlock` out of the result. More robust across
   game updates and removes the server dependency; the cost is a genuine behavioural
   divergence from the other two platforms, which would need Noah's buy-in to upstream.
   **This is the recommended path.**
3. **Get macOS added upstream** — a `MacOs` enum member, a server route, and an offset
   Noah derives from the Mac binary. The right long-term answer, entirely outside your
   control, and not something to block on.

**Practically:** treat options 1 or 2 as the local answer, and don't let this stop the
port. The editors, mod manager, spawnset/asset/replay tools have no memory dependency
whatsoever and are unaffected by every word of this section.

---

## 5. Permissions problem #3 — the unattended agent loop

`.claude/settings.json` in this repo scopes what an unattended `loop.sh` iteration may
do. It is deliberately narrow, so you never need
`PERMISSION_MODE=bypassPermissions` — which approves *everything*, including network
calls and `rm -rf`.

What's allowed: file reads/edits, `git` except `push`, and builds. What's explicitly
denied: `git push`, `sudo rm`, `rm -rf /*`, `gh repo delete`, and reading `.env`,
`credentials*`, `*secret*`.

Two macOS-specific additions beyond the stock template:

- `Bash(~/.dotnet/dotnet:*)` and its expanded forms — **without these every build in
  every iteration is denied**, the agent asks for approval nobody is there to give, and
  the loop's stall guard kills the run after two empty iterations. See §1.
- `otool`, `codesign`, `nm`, `file`, `uname`, `sw_vers`, `pgrep` — for inspecting Mach-O
  binaries and signing state.

`git push` stays denied on purpose. An unattended agent should be able to commit as much
as it likes; deciding what leaves this machine is yours.

### Sudo and the loop do not mix

**An unattended loop cannot run `sudo`.** There is nobody at the keyboard to type a
password. This means the `task_for_pid` proof can never be a passing loop iteration — an
agent can *write* the memory service, and the compiler can prove it *builds*, but only a
human with the game running can prove it *works*.

Plan around that: the loop does the mechanical port, and live-memory verification is a
manual gate you run yourself afterwards.

---

## 6. Git remotes

This clone was re-pointed so `origin` is your fork:

| Remote | URL |
|---|---|
| `origin` | `https://github.com/zeno-blade-creator/ddinfo-tools` (public fork) |
| `upstream` | `https://github.com/NoahStolk/ddinfo-tools` (original) |

Pull upstream changes with `git fetch upstream && git merge upstream/main`.
`/specs/*` is gitignored — that's loop execution state, not project source, and an agent
that treats `spec.json` as project state to keep updated will have its work discarded by
the loop's spec-protection gate.

---

## 7. Verification commands

What runs green **today**, on macOS, unmodified:

```bash
~/.dotnet/dotnet build src/DevilDaggersInfo.Tools.Engine/DevilDaggersInfo.Tools.Engine.csproj
```

(5 pre-existing `CA1819`/`CA1724` warnings, 0 errors. Not introduced by this work.)

What does **not** build yet, and becomes the finish line:

```bash
~/.dotnet/dotnet build src/DevilDaggersInfo.Tools/DevilDaggersInfo.Tools.csproj
```

This distinction matters for the loop: spec-level `verification` commands run after
*every* issue, so only the first belongs there for now. The second has to be a per-issue
check on the ticket that fixes it, and can be promoted to spec level once it's green.

---

## 7a. Known issues — follow-up work

### Crash when a run ends (open, needs one reproduction)

Observed 2026-08-22 14:16 under `sudo`, with live Run Analysis working correctly. The app
died with `SIGABRT` (Abort trap: 6) the moment the in-game run ended. `SIGABRT` from a .NET
process means an **unhandled managed exception**; the message goes to stderr and never
reaches Serilog, which is why `~/ddinfo-0.13.7.1.log` ends cleanly at:

```
14:14:21 [INF] Found the ddstats block at 0x30A0011D0 in Devil Daggers (process 36486),
               after reading 624 MB across 1189 memory regions.
```

Crash report: `~/Library/Logs/DiagnosticReports/ddinfo-tools-2026-08-22-141603.ips`.

**To fix this, someone must first capture the stderr**, which is the one thing not
recorded anywhere:

```bash
cd ~/Claude/Projects/ddinfo-tools
sudo DOTNET_ROOT="$HOME/.dotnet" \
  ./src/artifacts/bin/DevilDaggersInfo.Tools/debug/ddinfo-tools 2>&1 | tee ~/ddinfo-crash.txt
```

Then start a run, die, and read `~/ddinfo-crash.txt`.

**Leading hypothesis**, to be confirmed or discarded against that output —
`GameMemoryService.GetStatsBuffer()`:

```csharp
byte[] buffer = new byte[StatsBufferSize * MainBlock.StatsCount];   // 112 * int, in int arithmetic
```

`StatsCount` is read straight out of game memory with no bound check, and this overload is
called from `RecordingLogic.cs:200`, which runs **when a run completes** — matching the
symptom exactly. A garbage `StatsCount` overflows `112 * StatsCount` past `int.MaxValue`
into a negative, and `new byte[negative]` throws on the render thread.

Why macOS specifically: it is the only platform whose block address is *scanned and
cached* rather than read fresh from a game-maintained pointer. If the game moves or
rebuilds the stats array at run end, the cached block can still pass `MainBlock.IsValid`
(marker, format version, name terminators) while `StatsBase`/`StatsCount` have gone stale.
Windows and Linux re-read the pointer every frame and cannot land in that state.

If confirmed, the fix is small: bound-check `StatsCount` before allocating (the existing
`IsReplayValid()` already does exactly this for `ReplayLength`, rejecting
`<= 0 or > 30 * 1024 * 1024` — the same treatment applied to `StatsCount` would do), and
re-scan rather than trust a cached block whose derived pointers stopped making sense.

### Scan freezes the UI (open, measured)

`Scan()` runs on the render thread. A cold scan measured 16 s, 46 s, 49 s and 57 s in four
runs; the real game took 624 MB / 1189 regions and was much faster, but the worst case
stands. Moving the scan to a background thread is the obvious follow-up.
`OSXMemoryService` is not thread-safe today — the task-port cache, `_blockBuffer` and
`_scanBuffer` are all plain fields.

### Wrong advice for one macOS case (open, cosmetic)

`ReplayEditorMenu.DescribeGameMemoryUnavailable` only consults `DescribeUnavailability()`
for `MemoryUnreadable`; for `BlockNotFound` it falls back to "Make sure the game is
running", which on macOS is exactly wrong — the game *is* running and its memory *was*
read.

## 8. Explicitly out of scope

Say this on day one and don't let it creep:

- Code signing, notarization, or applying for the debugger entitlement
- Shipping a distributable `.app` or a macOS release pipeline
- Any change to game asset formats or the leaderboards API
- Upstreaming the PR — do that after it works locally
- x86_64 macOS. This is arm64 only.
