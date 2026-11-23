# Ionea launcher: notes for coding agents

C# / WPF on .NET 10, one project (`Launcher.csproj`, namespace `Ionea`, exe `Launcher.exe`). Everything is drawn on
a 1920x1080 stage inside a Viewbox; movement uses render transforms and opacity only.

## Layout

| Path | What |
|---|---|
| `src\` | all launcher code, one folder (map below) |
| `definitions\` | shipped emulator, core, port and system definitions (JSON), copied next to the exe |
| `assets\` | icon, intro art, UI sounds (embedded resources) |
| `test\*.txt` | script tests, run by the launcher's `--script` runner (`src\TestScript.cs`) |
| `test\*.ps1`, `test\*.py` | set-up scripts: make a test folder, run a script test, check the result |
| `test\unit\` | logic tests with no framework (`Ionea.Tests.csproj`, `Program.cs`) |
| `test\TestGame.cs` | a harmless window that closes after 3 s, used as a fake game |
| `tools\` | `Make-Release.ps1`, definition import (`import_defs.py`, `dry_discover.py`), sound generators |
| `Install-Launcher.ps1` | build + install (or `-BuildOnly <dir>`), stamps the build number from `build.txt` |
| `installer\Ionea.iss` | the Inno Setup 6 installer script (built by `tools\Make-Release.ps1`) |

## Source map (`src\`)

- **Start and window**: `Core.cs` (entry, `--` switches, one instance per data folder, log), `MainWindow.cs`
  (screen stack, input routing, full screen / window), `Screens.cs` (start screen), `Intro.cs`, `Outro.cs`,
  `Sleep.cs`, `Toasts.cs`, `Tutorial.cs`.
- **Look and UI**: `Look.cs` (palette, fonts, animation, art, sounds), `Sound.cs` (UI mixer), `Cards.cs`
  (`MenuCard`, Settings, messages, lists), `Toggle.cs`, `Pointer.cs` (own cursor, mouse routing), `Osk.cs`
  (on-screen keyboard), `Picker.cs` (file browser), `Home.cs` (home row), `HoldMenu.cs`, `ContainerCards.cs`,
  `CoverColour.cs`, `CoverZoom.cs`, `Covers.cs` (cover downloads), `Loc.cs` (UI text by id, languages).
- **Data**: `Data.cs` (settings, library JSON, `.lnk`, ES-DE import), `Paths.cs`, `Profiles.cs`,
  `ProfileScreens.cs`, `ProfileStats.cs`, `Storage.cs` (places, verified moves).
- **Games and emulators**: `Emulators.cs` (emulator list, ROM logic), `EmuDefs.cs` (definitions loader, levels),
  `EmuCards.cs`, `EmuInstall.cs`, `EmuOpen.cs`, `EmuControls.cs` (controls written into standalone emulators),
  `Embed.cs` (standalone emulator windows inside Ionea's, job object), `Cover.cs` (a still over a game window while
  the Home menu opens), `DisplayCopy.cs` (DXGI screen copy), `Launch.cs`, `HomeMenu.cs`, `SwitchGame.cs`,
  `Quiet.cs`, `Tweaks.cs`, `Ports.cs` (native ports, shared saves), `Helper.cs`, `Steam.cs`, `SteamCards.cs`,
  `AddGame.cs`, `Scan.cs`, `ScanCards.cs`, `RequiredFiles.cs`, `GameInfo.cs`, `GameId.cs`, `SwitchRead.cs`,
  `RomArt.cs`.
- **Hosted cores**: `Libretro.cs` (core host, environment calls, options, save RAM, states, waveOut fallback),
  `CoreGame.cs` (core thread, picture, input, core settings), `CoreSound.cs` (WASAPI, sound queue, rate control),
  `GlHost.cs` (OpenGL context and readback), `GlReadback.cs` (readback ring, row copy), `VulkanHost.cs`
  (Vulkan device and readback), `VulkanFrames.cs` (sync-index ring, pixel formats), `DirectPicture.cs`
  (Direct2D child window), `DisplaySync.cs` (`FrameTiming`, `VBlankClock`), `Display.cs`
  (game display modes, game window), `Overlay.cs`, `FrameSource.cs`, `LatencyProbe.cs`, `FastForward.cs`
  (`PlayPrefs`, speed and picture-skip logic, the fast-forward and rewind settings card), `Rewind.cs`
  (`RewindBuffer`: packed state steps in a fixed-size ring), `RunAhead.cs` (`RunAhead`, `AheadBudget`: which games
  run ahead, when it gives up), `PictureFilter.cs` (`PictureFilters`: which filter a game gets, its pattern tile).
- **Input**: `Input.cs` (keyboard map, XInput, Win32 helpers), `HidPads.cs` (non-Xbox controllers via raw input),
  `InputDevices.cs`, `InputSetup.cs` (`InputPrefs`, `InputMap`), `InputCards.cs`, `PlayerSlots.cs`,
  `LauncherKeys.cs` (launcher keys, the Home menu key hook).
- **Network and updates**: `Online.cs` (the one download path, switches, log), `Updater.cs`, `Discord.cs`.
- **Tests**: `TestScript.cs` (`--script` runner, `--dummies` generator).

## Build

```powershell
dotnet build -c Release                                     # Launcher.exe in bin\Release\net10.0-windows\
powershell -File Install-Launcher.ps1 -BuildOnly <dir>      # single-file publish into <dir>, nothing installed
```

`Install-Launcher.ps1` without `-BuildOnly` installs and raises `build.txt`; don't run it just to build.
Its publish is self-contained (win-x64, single file, ReadyToRun, not trimmed: WPF and reflection), about 150 MB;
`-FrameworkDependent` makes the old small exe that needs the .NET 10 Desktop Runtime. The layout is the same either
way: `Launcher.exe`, `definitions\`, `THIRD-PARTY.md`, `CHANGELOG.md`.

## Release

1. Raise `VersionPrefix` in `Launcher.csproj` when the version changes; move the changelog's "Not released yet"
   lines under the new version.
2. `powershell -File tools\Make-Release.ps1 [-Out <dir>]` (default `bin\release`): builds the next build number
   (raises `build.txt`; commit it) and writes `Ionea-<version>.zip` (portable, and what the updater downloads),
   `Ionea-<version>-setup.exe` (only when Inno Setup 6's `ISCC.exe` is found: `-Iscc <path>`, PATH or its usual
   folders; otherwise a warning and no installer) and `SHA256SUMS` for both. `-NoInstaller` skips the installer.
3. Check the build: `test\rel1-start.ps1 -Exe <unpacked zip>\Launcher.exe` (starts with no .NET on PATH, first
   run to Home, nothing loaded from a shared .NET install).
4. Publish the three files as a GitHub release tagged `v<version>` (the tag the script prints). Keep a single
   SHA-256 list: the updater takes the first asset with `sha256` in its name and finds the zip's line in it.

The installer (`installer\Ionea.iss`): per user, no admin, `%LOCALAPPDATA%\Programs\Ionea` (so the data folder is
`<install>\data`), Start menu shortcut, desktop shortcut as a task left unticked, closes a running Ionea first. An
install over an older one keeps that exe and definitions as the previous version (Go back). The uninstaller removes
the program and Ionea's own leftovers (`Launcher.previous.exe`, `definitions.previous\`, `update\`) and asks
whether to remove `data\` too (No is the default, and a silent uninstall keeps it). Its `AppId` GUID must never
change; `Updater.cs` uses it to keep the installed version shown in Windows' app list.

## Tests

- Unit tests: in `test\unit`, `dotnet run -c Release` runs all; `dotnet run -c Release -- "display sync"` runs one
  (names are listed in `Program.Main`: `names`, `playlists`, `download check`, `move`, `data files`, `links`,
  `pad reports`, `game sound`, `players`, `display sync`, `gl readback`, `vulkan frames`, `fast forward`, `rewind`, `run ahead`, `picture filter`, `language`, `pause way`, `tool path`, `streaming mode`). Exit code = failures. They work in
  `%TEMP%\Ionea-tests\unit`.
- Script tests: `Launcher.exe --data <dir> --no-import --windowed --mute [--dummies N] [--lang <id>] --script test\<name>.txt`.
  Results are `SCRIPT ok ...` / `SCRIPT FAIL ...` lines and a final `SCRIPT done, N failed` in
  `<data>\launcher.log`. The header of each `.txt` says how to run it (dummies, set-up script).
- A test with a `.ps1` / `.py` of the same name needs it: it makes its folder under `%TEMP%\Ionea-tests\<name>`,
  sets `IONEA_TESTROOT` (and `IONEA_TESTROOT2` for a second folder), then runs the script. In scripts, `T\...` and
  `T2\...` are paths inside those folders and `HERE\...` is next to the script.
- Tests against a real install read its place from the environment, never from a fixed path: `ESDE\...` in a
  script is inside ES-DE's folder (`IONEA_ESDE`, else the run's `--esde` / Settings > ES-DE folder) and `INBOX\...`
  inside the Inbox (`IONEA_INBOX`, else `--inbox` / the setting). `p4-allembed.ps1 -Esde` defaults to `IONEA_ESDE`;
  `h0`, `h1`, `h2` take `-Install` (default `IONEA_INSTALL`, else the installer's `%LOCALAPPDATA%\Programs\Ionea`)
  and use its `Launcher.exe` and `data\`.
- `test\w7-fresh.ps1 -Core <mgba_libretro.dll>` checks a fresh build in an empty folder end to end.
- Never take the user's input: tests run `--windowed`, never topmost or full screen, and send no real key events.
  `h0`, `h1`, `h2`, `c2-realkeys` and `k4-home-key-ingame` use real input or real games: only with the owner's OK.

## Conventions

- **Neutral product.** Code, UI text, comments, definitions, docs and tests contain no personal names, paths,
  machines, setups or references to private notes or plans. Test paths are relative to the script or under
  `%TEMP%\Ionea-tests\`. Example addresses use documentation ranges (`192.0.2.x`).
- No `*.bak` files or copies of files under git: commit instead. Small commits, one change each.
- Keep each file's existing encoding and line endings (many `.cs` files are UTF-8 with BOM; `CHANGELOG.md` is
  UTF-8 with BOM and CRLF). Don't re-save whole files in another format.
- `.gitignore` ignores the top level by default; a new top-level file must be listed there.
- Nullable reference types are on (`<Nullable>enable</Nullable>`). New files must be nullable-enabled: no
  `#nullable disable`. Older files that start with it predate the switch; enable one (remove the line, fix its
  warnings) when it is quick. The build stays warning-clean.
- User-facing changes get a line in `CHANGELOG.md` (newest first, plain words, no internal names).
- UI text: short, plain, the same word for the same thing everywhere.

## UI text and languages

- Every user-visible string (cards, menus, settings rows, toasts, errors, hints) comes from
  `assets\lang\en.json` by a stable id: `T("settings.theme")`, `T("errors.preparing", name)` (fills `{0}`, `{1}`),
  `N("settings.games", count)` for counts (an object `{ "one": "{0} game", "other": "{0} games" }`; `{0}` is the
  count). Files using them have `using static Ionea.Loc;`. Not translated: log lines, script test commands, file
  names, key and button names used as ids, and definition data (system and emulator names).
- Adding a string: put it in `en.json` (UTF-8, no BOM, LF; ids are `<area>.<words>`: `common.`, `home.`, `menu.`,
  `settings.`, `info.`, `errors.`; reuse an existing id for the same text), then use `T("id")`. A missing id shows
  the id itself and is logged once. `python tools\loc_extract.py --dry <area> <files>` lists literal text in the
  given files and the ids it would get; without `--dry` it moves them (`--loose` also takes sentences outside the
  known UI calls; `file.cs:10-90` limits it to lines). Check its "by hand" list and the diff: it must not touch
  strings used as keys or compared.
- Code that finds a row or category by its label compares the English text: use `Loc.ToEnglish(label)`.
- Adding a language: copy `en.json` to `assets\lang\<id>.json` (`de`, `pt-BR`; embedded at build time) or put it
  in `<data>\lang\<id>.json`, set `"_name"` to the name shown in Settings > Look > Window > Language, translate
  the values and keep ids, `{0}` placeholders and plural keys. Missing ids fall back to English. "Automatic" picks
  the Windows display language (`pt-BR`, then `pt`) when there is a file for it, else English.
- Checking: `--lang xx-pseudo` (or Language "Pseudo (test)") shows every string bracketed, accented and about
  30 % longer; text that stays plain English was never moved, and text cut off needs trimming or wrapping.
  `test\l10n-pseudo.txt` runs it through Home, the Hold menu, every settings pane and a toast and takes screenshots.
- Static tables (presets, sizes, sources) keep the id and look the text up when shown (`Name => T(NameId)`): a
  `T()` in a static initializer runs before the language is set and never follows a change in Settings.

## Architecture

- **Hosted cores first.** New emulators are added as libretro cores Ionea hosts (picture, sound, input, Home menu,
  states and options are Ionea's). A standalone emulator is the fallback, chosen per game.
- **Core thread.** Each hosted game runs on its own thread (`CoreGame.Loop`, 16 MB stack). A crash inside a core
  takes the launcher down (same process). On quit, the core is stopped and its GL context destroyed before the
  process ends; a live context at exit can leave a thread stuck in the driver.
- **GL readback.** `GlHost` makes a hidden window and context on the core thread for `SET_HW_RENDER` (OpenGL /
  GL core). With GL 3.2+ frames are read back asynchronously through a ring of three
  pixel buffers with fences, showing the frame copied one frame earlier; otherwise, or on any failure, a direct
  `glReadPixels`. `IONEA_GL_READBACK=blocking` forces the direct way. Bottom-up frames get a negative pitch so the
  one row copy (`FrameRows`) flips them.
- **Vulkan readback.** `VulkanHost` serves `RETRO_HW_CONTEXT_VULKAN` (env 41, 43, 73): vulkan-1.dll is loaded only
  then (missing: the core is refused with a message). After `retro_load_game` the instance and device come from the
  core's negotiation interface (create_instance / create_device2, else create_device), else Ionea makes them, then
  `context_reset`. Three sync indexes, each with a fence, a command buffer and a host-visible buffer: each frame the
  core's image (`set_image`) is copied into one, and the newest earlier finished copy is shown through the usual
  frame path (one frame later, like GL). A fence over 5 s or device lost ends the game with a message. Close order:
  wait idle, `context_destroy`, unload, deinit, the core's destroy_device, then the device and instance. Cores that
  only draw with Direct3D are refused with a message. Future: share the image with D3D (zero copy) instead of reading back.
- **Display sync.** `FrameTiming.Decide(coreFps, displayHz)` picks n refreshes per frame when the core is close to
  the refresh rate (or an exact multiple); `VBlankClock` waits for the monitor's vblanks. The sound is resampled by
  the same skew (`CoreSound.SetSkew`, held to 5 %). Setting `Settings.SyncToDisplay`.
- **Run-ahead.** One core instance (`PlayPrefs.RunAhead`, 0-4 frames, off by default): the real frame runs with
  sound and no picture, its state is kept, N more frames run with the same input (`holdInput`), no sound and only
  the last picture (`hideVideo`, `RetroCore.AvEnable` for GET_AUDIO_VIDEO_ENABLE), then the state goes back. Not
  for GPU-drawn cores or the incomplete-state quirk; gives up for the game (toast) when a state fails or
  `AheadBudget` sees too many frames over 3/4 of the frame time. Precedence: rewind > fast-forward > run-ahead
  (paused while either is on); display sync is unchanged (it paces the shown frames). Rewind reuses run-ahead's state.
- **Picture path.** `DirectPicture` draws a core's frame straight to the screen through a Direct2D child window
  while nothing of the launcher is over it; otherwise it goes on `GameLayer`.
- **Streaming mode.** `Settings.StreamingMode` (off by default) is for window capture (Discord, OBS), which sees only
  the launcher's window and its children: `EmbedLogic.Owned` gives standalone emulators the child-window way (an
  SDL child can't capture the mouse) and `DirectPicture.Off` leaves core pictures to WPF. Embedding is decided at
  each game's start; turning it on mid-game moves a core's picture to WPF at once. `test\st1-streaming.ps1` checks
  it with PrintWindow (`-OnScreen` for the core: WPF draws nothing off-screen).
- **Picture filters.** `PictureFilters.For(w, g, hardware)`: the game's `Display.Filter`, else its system's
  (`Settings.Filters[system]`), else `Settings.Display.Filter`, else automatic (sharp, smooth for GPU cores). Sharp
  and LCD scale nearest, smooth and CRT linear. CRT and LCD lay a black premultiplied tile, one per game pixel, over
  the picture: a bitmap brush in `DirectPicture` (`Pattern`), a tiled `ImageBrush` on `CoreSession.veneer` for WPF.
  No pixel shaders or .slangp presets yet (future: a Direct2D effect or a D3D11 path). Screenshots are unfiltered.
- **Input and players.** XInput slots 0-3 and HID pads (`HidPads`) in slots 4-7, all read as Xbox-style state.
  `PlayerSlots` gives each controller its own player (port N = slot N, filled in the order pads are seen; an
  unplugged pad frees its slot only). Game bindings (`InputMap`) are separate from launcher keys (`LauncherKeys`).
  The Home menu key is a low-level keyboard hook (`HomeKeys`), claimed only while a game or its menu is in front,
  never in test runs; a lone tap of a modifier fires, so Alt+Tab still works. The other in-game keys
  (`LauncherKeys.InGame`: fast-forward, rewind, screenshot) are F-keys read by `CoreSession.Poll`, plus a fixed View + button combo


  on Player 1's pad only (`LauncherKeys.PadCombo`); game buttons can't share a key with them.

- **Standalone emulators.** Started directly, their window owned by and fitted into Ionea's (`Embed.cs`). The Home
  menu holds them by `PauseLogic.Decide`: the definition's `hotkeys.pause` (a toggle, posted on open and on resume),
  else they keep running behind the menu, out of focus. `NtSuspendProcess` (undocumented; can upset sound and
  controller drivers) only with `Settings.FreezeEmulator` (Freeze the emulator, off by default). Steam and PC games
  are never paused or injected into.
- **Definitions.** `definitions\` (shipped) plus `<data>\definitions\` (the user's, wins field by field by id).
  Levels Generic / Known / Managed / Core; a version outside `tested` drops one level, which only stops Ionea
  writing that emulator's config. No per-emulator branches in code: behaviour comes from the definition.
- **Network.** `Online.Download` is the only way out: refused while Offline mode is on or the item's switch is
  off, https only, host checked on every redirect, programs confirmed every time, every attempt logged.
- **Updater.** From a GitHub release (zip with Launcher.exe at its top and a SHA-256, required) or a source folder.
  It swaps the exe (keeping `Launcher.previous.exe` and `definitions.previous\`), never touches `data\`, and is
  refused while a game runs. It works the same in an installed or a portable copy (the folder the exe runs from);
  in an installed one it also sets the version in the installer's uninstall entry. It never runs the setup exe. An access token is stored with DPAPI and sent only to api.github.com.
- **Data safety.** Settings and library are written through a temp file and swap, with the previous copy kept;
  moves copy, hash and compare before deleting; save links replace only links Ionea made.

## Pitfalls

- `Install-Launcher.ps1` clears `obj\Release\net10.0-windows\win-x64` before publishing; stale resources otherwise
  ship in the single-file exe.
- The single-file exe starts more slowly than the `dotnet build` output; scripts that run against it need a longer
  first `wait`.
- One launcher per data folder: a second start with the same `--data` only brings the first one's window forward.
- A move inside one drive is a rename, so copy, verify and cancel paths are only exercised across drives (see
  `test\e1-storage-reset.py --other`).
- melonDS DS lists its core options only while a game runs; its rows come from the definition until then.
- A press set as the Home menu key is never sent to a game (`InputPrefs.KeyFor` drops it).
