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
| `task_for_pid` on the game | ⚠️ Unproven — needs `sudo`; run the probe |
| macOS marker offset from devildaggers.info | ❌ **Does not exist. Not obtainable.** |

The editors, mod manager, and replay tools are a straightforward port.
**Practice mode / live stats has a dependency you cannot satisfy alone.**

---

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

To fix it permanently for your shell (optional, does not help unattended processes):

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

### Running the probe

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
   does the hard half — it finds the block by scanning and then locates the pointer to it.
   Fine for your own machine. Brittle: it breaks on every game update.
2. **Scan instead of fetch, on macOS only.** Search the address space for `__ddstats__`
   directly and skip the offset entirely. More robust across game updates and removes the
   server dependency, but it's a genuine behavioural divergence from the other two
   platforms and would need Noah's buy-in to upstream.
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

## 8. Explicitly out of scope

Say this on day one and don't let it creep:

- Code signing, notarization, or applying for the debugger entitlement
- Shipping a distributable `.app` or a macOS release pipeline
- Any change to game asset formats or the leaderboards API
- Upstreaming the PR — do that after it works locally
- x86_64 macOS. This is arm64 only.
