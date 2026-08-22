# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Cross-platform Devil Daggers modding tools, practice tools, and custom leaderboards client. A single ImGui desktop app that runs on Windows and Linux — and, from source, on macOS (arm64) — and bundles what used to be several separate Windows-only tools (Survival/Spawnset Editor, Asset Editor, Custom Leaderboards, Mod Manager, Replay Editor, Practice).

## Toolchain

- .NET SDK `10.0.100` or later (`global.json` rolls forward `latestMajor`).
- C# `LangVersion=14.0`, `Nullable=enable`, `WarningsAsErrors=nullable`, `AnalysisMode=All`, `InvariantGlobalization=true` (set in `src/Directory.Build.props`). Treat nullable warnings as build-breaking.
- Analyzers enabled repo-wide: Nullable.Extended, Roslynator, SonarAnalyzer.CSharp, StyleCop.Analyzers.
- Solution file is **`.slnx`** (XML), not `.sln`: `src/DevilDaggersInfo.Tools.slnx`.
- `.editorconfig`: `.cs` files use **tabs** (size 4). `.csproj`/`.yml` use 2-space indent.

## Common commands

**Invoke the SDK by absolute path: `~/.dotnet/dotnet`.** On this machine bare `dotnet` resolves to `/usr/local/share/dotnet`, which cannot see the .NET 10 SDK and fails with "A compatible .NET SDK was not found". Every command below is written that way for a reason.

Run the app from the repo root:

```bash
~/.dotnet/dotnet run --project src/DevilDaggersInfo.Tools/DevilDaggersInfo.Tools.csproj
```

Build / test the whole solution (mirrors CI):

```bash
~/.dotnet/dotnet build src/DevilDaggersInfo.Tools.slnx -c Release
~/.dotnet/dotnet test  src/DevilDaggersInfo.Tools.slnx -c Release --no-build
```

There is no test project in the solution today — `dotnet test` is a no-op kept for parity with CI (it exits 0 with no output; that is not a hang). Don't assume tests exist.

**On macOS, build the two projects individually in Debug instead:**

```bash
~/.dotnet/dotnet build src/DevilDaggersInfo.Tools.Engine/DevilDaggersInfo.Tools.Engine.csproj
~/.dotnet/dotnet build src/DevilDaggersInfo.Tools/DevilDaggersInfo.Tools.csproj
```

The `-c Release` solution build fails on macOS with four errors (`Container.cs` CS0161 ×3, `ConfigLayout.cs` CS0103): in Release the platform constant comes from `RuntimeIdentifier`, and with no RID set none of `WINDOWS`/`LINUX`/`OSX` is defined, so no platform arm compiles at all.

A full rebuild of the app project reports ~107 warnings, all pre-existing — mostly CA1812 "never instantiated" for the platform classes the current host does not construct. Compare against that count to tell a real regression from the noise, and use `-t:Rebuild` to get it: an incremental build with nothing to do reports 0 warnings.

Produce a release build (single-file, trimmed, self-contained):

```bash
# Linux helper:
scripts/build-release.sh
# CI publishes both win-x64 and linux-x64 with the same flags from .github/workflows/release.yml,
# triggered by pushing a `v*` tag.
```

## Project layout

Three projects, all under `src/`:

- **`DevilDaggersInfo.Tools`** — the application. Entry point `Program.cs` constructs a `Container` (StrongInject) and runs `Application`. Output assembly name `ddinfo-tools`.
- **`DevilDaggersInfo.Tools.Engine`** — thin rendering/input layer over Silk.NET (GLFW + OpenGL) (`Shader`, `Texture`, loaders, math helpers, intersections). Does not reference ImGui; the binding lives on `DevilDaggersInfo.Tools`.
- **`DevilDaggersInfo.Tools.Engine.Content`** — asset/content types (`MeshContent`, `ModelContent`, `ShaderContent`, `TextureContent`, parsers). Empty `.csproj` — picks up everything from `Directory.Build.props`.

External NuGet dependencies of note: `DevilDaggersInfo.Core` (spawnset/mod/replay parsers, AES encryption), `DevilDaggersInfo.Web.ApiSpec.Tools` (server contracts), `Silk.NET.{GLFW,OpenGL}`, `Hexa.NET.ImGui`, `StrongInject`, `Serilog.Sinks.File`, `NativeFileDialogSharp`, `SixLabors.ImageSharp`.

## Architecture you need to know before editing

**Compile-time DI via StrongInject.** All app singletons are registered as attributes on `Container` (`src/DevilDaggersInfo.Tools/Container.cs`). Adding a new window/service usually means adding a `[Register<T>(Scope.SingleInstance)]` line there *and* taking the dependency through a constructor. `Application` is the root; `Program.cs` resolves it via `Owned<Application>`.

**`Root` is a legacy global and is `[Obsolete]`.** New code should take dependencies via constructor injection through `Container`. `Root` still exposes a Serilog `Log`, the loaded ImGui fonts, an `AesBase32Wrapper`, and the platform-specific `GameMemoryService` / `GameWindowService` / `IPlatformSpecificValues` — prefer to leave those calls alone rather than expand them.

**Platform compilation is driven by `WINDOWS` / `LINUX` / `OSX` `DefineConstants`** set in `DevilDaggersInfo.Tools.csproj`. In `Debug`, the constant follows the build host OS; in `Release`, it follows `RuntimeIdentifier` (with a CI fallback to `LINUX`). `OutputType` switches between `WinExe` and `Exe` accordingly. Anywhere you see `#if WINDOWS` / `#elif LINUX` / `#elif OSX`, there must be a parallel implementation under `NativeInterface/Services/{Windows,Linux,OSX}/` implementing `INativeMemoryService` / `INativeWindowingService`, plus an `IPlatformSpecificValues` under `Platforms/`. Keep all three sides in sync.

**Only four files are allowed to switch on platform**: `Container.cs`, `ContentManager.cs`, `Ui/Config/ConfigLayout.cs`, and the csproj. Everything else goes behind those three interfaces. Adding a `#if` anywhere else is the thing to push back on.

**Native file dialogs have a Wayland-specific path.** `Container.CreateNativeFileDialog` returns `NativeFileDialogWayland` when `XDG_SESSION_TYPE=wayland` (or `WAYLAND_DISPLAY` is set on Linux), otherwise `NativeFileDialog` (NativeFileDialogSharp). Both implement `INativeFileDialog`, and `Application.Main` polls `_nativeFileDialog.Update()` each frame.

**Main loop** (`Application.Run` → `Main` → `Render`) is capped at ~300 Hz with `Thread.Yield` and uses GLFW's `SwapInterval(1)` for VSync. ImGui is dockspace-based (`ImGui.DockSpaceOverViewport`). UI is organized by feature under `Ui/<Feature>/` (AssetEditor, CustomLeaderboards, ModManager, Practice, ReplayEditor, SpawnsetEditor, Main, Popups, Config). 3D scenes live under `Scenes/`.

**Trimming and AOT-ish constraints.**
- `EnableTrimAnalyzer=true`, `SuppressTrimAnalysisWarnings=false` — trim warnings are real and must be fixed, not ignored. Release publishes with `PublishTrimmed=True` + `PublishSingleFile=True` + `SelfContained=True`.
- `JsonSerializerIsReflectionEnabledByDefault=false` — every JSON (de)serialization must go through a source-generated `JsonSerializerContext`. The contexts live in `JsonSerializerContexts/` (`ApiModelsContext`, `AssetPathsContext`, `UserJsonModelsContext`). When you add a new serialized type, register it on the appropriate context.

**User data location.** `UserSettings` and `UserCache` persist to `Environment.SpecialFolder.LocalApplicationData/ddinfo-tools/` (`settings`, `cache`, `imgui.ini`). They are static singletons today (marked `// TODO: Rewrite to instance.`); load order in `Program.cs` is `UserSettings.Load()` → `UserCache.Load()` *before* the container is constructed, because `Container` reads `UserCache.Model` when creating the GLFW window and ImGui controller.

**Encryption secret.** `Root.AesBase32Wrapper` reads `Content/encryption.ini` as an embedded resource. The file is **not** in the repo — CI writes it from the `ENCRYPTION` GitHub secret before publish (see `release.yml`). Local debug builds run fine without it; anything that needs the wrapper falls back to `null` and logs.

**Networking** lives in `Networking/`. `ApiHttpClient` + `ApiResult`/`ApiError` are the transport; `Networking/TaskHandlers/` contains one file per server endpoint (fetch leaderboards, upload submissions, etc.) that wraps the call in an async handler the UI awaits.

**Game integration.** `GameMemory/` and `GameWindow/` are platform-agnostic services that delegate to the per-OS `INativeMemoryService` / `INativeWindowingService` to read live game state from a running Devil Daggers process. `GameMemoryServiceWrapper` is the DI-friendly shell around the static `Root.GameMemoryService`. Four rules that are easy to get wrong:

- **Nothing in an `INativeMemoryService` may ever throw.** `GameMemoryServiceWrapper.Scan()` runs from the ~300 Hz render loop, so an escaping exception takes the app down instead of reporting the problem. Return null / no-op, and log the reason once behind a `bool` field so the log does not fill at 300 lines a second. This includes native symbol resolution: an unresolvable P/Invoke throws at its *call* site, so wrap it in `try`/`catch (DllNotFoundException or EntryPointNotFoundException)`.
- **Finding the ddstats block is per-platform, and comes in two shapes.** Windows and Linux derive from `MarkerOffsetMemoryService`, which reads a pointer at an offset the DevilDaggers.info API supplies (`RequiresMarkerOffset => true`). macOS has no such API route, so `OSXMemoryService` scans the address space for the `__ddstats__` marker and implements `ResolveBlockAddress` itself.
- **Validate any scanned address with `MainBlock.IsValid` before `new MainBlock(...)`.** The constructor slices the 32-byte name fields up to their null byte and throws `ArgumentOutOfRangeException` on a buffer without one — on the render loop, that is a crash.
- **Gate shared UI on `GameMemoryService.BlockAddressStatus`, not on `IsInitialized`.** `IsInitialized` is also false when the game simply is not running, which happens on every platform, so branching on it changes Windows and Linux behaviour too. `MemoryUnreadable` and `BlockNotFound` are only ever produced by the macOS scan path, so branching on those two is dead code elsewhere by construction.

On macOS, reading or writing another process's memory requires root: practice mode's live stats, replay reading, and replay injection only work when the app is launched under `sudo`. Every other feature works without it, and the UI has to say which of the two it is rather than falling back to "make sure the game is running".

## Conventions

- **UI text is UTF-8.** `Hexa.NET.ImGui` takes `ReadOnlySpan<byte>` (or `string`), never `ReadOnlySpan<char>`. Pass static text as a `u8` literal (`ImGui.Text("Gems"u8)`) and dynamic text through `Inline.Utf8($"...")`. `Inline` writes into one shared static buffer, so only one `Inline` result may be alive at a time — never hold one across another `Inline` call, and never interpolate one into another (a `Debug.Assert` catches the latter). Types that only implement `ISpanFormattable` (enums, `DevilDaggersInfo.Core` numerics) go through `Inline.Utf8Formattable`. Format strings stay `ReadOnlySpan<char>` — they feed `TryFormat`, not ImGui.
- `Directory.Build.props` is the source of truth for `TargetFramework`, language version, nullable, analyzers, and `RuntimeIdentifiers`. Don't redefine these per project.
- Tab indentation in `.cs`. Match existing brace and using-order style — StyleCop will flag you otherwise.
- `[Obsolete]` markers (e.g. `Root`) are intentional — don't clear them by suppression; migrate callers off instead.
- The `CHANGELOG.md` is hand-edited. When changing user-visible behavior, add a bullet under `## [unreleased]` in the appropriate section.
- **Interpolated strings destined for a log go through `string.Create(CultureInfo.InvariantCulture, $"...")`.** SonarAnalyzer S6618 rejects `FormattableString.Invariant` and a bare `$"..."` risks CA1305. Serilog message templates themselves are fine as-is — it is only the arguments you build by hand.
- **Mach P/Invoke (macOS):** omit `SetLastError` — Mach reports failure through the `kern_return_t` return value, not `errno` — and pass `vm_region` info as an `int*` from a `stackalloc int[9]` rather than an `[Out] int[]`, which sidesteps `LibraryImport` array marshalling entirely. See `OSXMemoryService`.
- **StrongInject factory methods in `Container.cs` can take any already-registered singleton as a parameter** (e.g. `ILogger`) with no extra `[Register]`/`[Factory]` wiring.

## Testing platform code without a test project

The solution has no tests, but platform code can still be exercised for real. A throwaway console `csproj` with `<EnableDefaultCompileItems>false</EnableDefaultCompileItems>` plus `<Compile Include="../ddinfo-tools/src/.../Foo.cs" />` links the *actual* repo sources into a runnable probe (add `Serilog` + `Serilog.Sinks.Console` for the `ILogger` the services take). `~/Claude/Projects/ddmac-scan-probe` does this for `OSXMemoryService`, and caught a status-reporting bug the build could not.

For the macOS memory path specifically:

- The Mach bindings can be driven **without sudo and without the game running** by targeting our own pid: `task_for_pid(selfTaskPort, Environment.ProcessId)` succeeds, which is enough to prove signatures, marshalling, and region walks.
- A read of an unmapped address returns `kern_return_t` 1 (`KERN_INVALID_ADDRESS`) rather than crashing or signalling, so speculative probing is safe.
- Once the service has validated a block, its own read buffer holds a byte-identical copy, so a *second* self-scan legitimately finds it. Test "found" and "not found" in separate process runs, not by mutating a block mid-run.
- A full self-scan walks ~10 GB across a few hundred regions and takes tens of seconds, varying several-fold run to run with heap layout. Don't read a single timing as a regression, and budget for it before adding any other synchronous whole-address-space work on the render thread.
