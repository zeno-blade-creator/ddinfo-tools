# When can I walk away from the computer?

A quick-reference card for running the `/ship` loop. The answer flips exactly **once**, at
the moment you launch `loop.sh`.

The one thing to internalise:

> **`caffeinated` stops the Mac idle-sleeping. It cannot stop the lid switch.**
> Closing a MacBook sleeps it regardless. "Walk away" and "close the lid" are two
> different questions and they have different answers.

---

## The two states

### State A — nothing is running (grilling, spec, issues, reading, deciding)

**Walk away freely. Close the lid. Shut the laptop. Go to dinner. Come back tomorrow.**

Nothing is in flight. The conversation lives on Anthropic's servers, not your Mac.
Everything produced so far is a file on disk or a git commit. Closing the lid costs you
nothing at all.

This is where you are for everything up to and including `--check`.

### State B — `loop.sh` is running

**You can walk away. Do NOT close the lid.**

| Action | OK? | Why |
|---|---|---|
| Walk away, lid open | ✅ | This is the entire point. `caffeinated` holds off idle sleep |
| Let the screen go dark | ✅ | Deliberate — `caffeinated` omits the display flag to save power |
| Unplug from power | ⚠️ | It keeps running but drains. Stay plugged in for long runs |
| **Close the lid** | ❌ | Mac sleeps, run freezes |
| Ctrl-C to stop early | ✅ | Safe. Resume later by rerunning the same command |

### Can I use the computer while it runs?

**Yes.** It's a terminal process running `claude` and `dotnet build`. It never takes focus,
never takes over the screen, and does not need the game running. Play games, browse, work.
A heavy game competes for CPU and slows builds — that is the only effect, and it is not a
correctness risk.

Three exceptions:

| Don't | Why |
|---|---|
| Close the lid | Mac sleeps, run freezes |
| Quit or close the Terminal window running it | Kills the process |
| **Edit any file inside this repo** | `loop.sh` fails an attempt if it finds uncommitted changes it didn't make. Saving a file mid-run can fail an otherwise-good iteration |

Anywhere else on the Mac is fair game. Just treat this folder as off-limits until it's done.

---

## What "closing the lid mid-run" actually costs

Less than you'd fear. It is **not** data loss.

`loop.sh` keeps all state in `spec.json` and writes it after every iteration. When you
reopen:

- Everything **already committed** is fine. Each completed issue is a real git commit and
  a `done` entry in the spec.
- The **single iteration in flight** is lost. It stays marked `claimed` — a few minutes of
  work and tokens.
- Resume by **rerunning the exact same command.** The loop re-picks the claimed issue and
  replays its failure into the next attempt.

**Do not hand-edit a `claimed` status to "fix" it.** The loop owns that field. Editing
`spec.json` mid-run causes the iteration to be reverted and failed outright.

The real cost isn't corruption — it's coming back expecting a finished port and finding
the run frozen at issue 2.

---

## If you genuinely need the lid closed

Only one legitimate option: **clamshell mode** — external display, external
keyboard/mouse, and connected to power. All three, or the Mac sleeps anyway.

There is a `sudo pmset -b disablesleep 1` trick. Don't. It changes system power behaviour
globally, it's easy to forget you set it, and a laptop that never sleeps in a bag gets
hot. Not worth it to save leaving a lid open.

---

## The command

```bash
~/.claude/scripts/caffeinated ~/.claude/scripts/loop.sh specs/macos-port
```

No `--cost` ceiling: extra/overage usage is turned off at the account level, which is a
harder stop than the script's own dollar accounting and is the guard that actually matters.
The loop also self-limits — 50 iterations max, stops after 2 iterations that complete
nothing, and circuit-breaks after 3 failures on one issue.

This repo has a scoped `.claude/settings.json`, so `PERMISSION_MODE=bypassPermissions`
should not be needed. If iterations stall asking for approval nobody is there to give, add
that prefix and understand what it means: the agent runs anything without asking.
`git push` is denied either way.

---

## Before you start a long run

- [ ] Lid open, plugged in
- [ ] Turn **off** extra/overage usage in Claude account settings, so the run stops at
      your plan limit instead of billing credits. `--cost` is the script's own dollar
      accounting and knows nothing about your 5-hour window.
- [ ] `loop.sh specs/macos-port --check` passes
- [ ] Devil Daggers **not** required — no iteration needs the game running

## After it stops

Live verification is a **manual gate** — an unattended loop cannot run `sudo`, so no
iteration can prove memory reading actually works. Once the run finishes: launch the game,
start a run, and launch the tool with `sudo` to confirm live stats appear.
