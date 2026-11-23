# Ionea changelog

Newest first. Settings > About and updates shows this file from the update source.

## Not released yet
- The focus ring on the Home buttons and the profile picture now sits right against the icon, with no gap, in every Icon shape (round, rounded square, square).

## 1.0.6 (2026-11-23)
- Two games with the same file name in different folders no longer swap saves between profiles. Each has its own saves per profile; saves made by an earlier version are found and kept.
- Close game no longer ends a slow emulator after a few seconds, which could cut off a save. If it is still closing after 15 seconds, Ionea asks: Keep waiting (the default) or End it now. Quitting Ionea during a game waits the same way.
- A download that stops getting data for a minute is given up instead of waiting forever.
- The release and the installed folder now include the LICENSE file, and THIRD-PARTY.md lists the .NET runtime built into Ionea and its licence.

## 1.0.5 (2026-11-23)
- Moving a game to another folder on the same drive: if a file can't be moved part way (in use by another program), the files already moved go back to where they were. Before, they could be deleted.
- Moving or uninstalling a multi-disc game (.m3u) now takes each disc's tracks too (the .bin files its .cue names), so the game still plays in its new folder. Dreamcast .gdi tracks whose names have spaces are found too.

## 1.0.4 (2026-11-23)
- Offline mode is now the Network connection switch in Settings > Network, like a Wi-Fi switch: off by default, and Ionea goes online only while it is on. Your current choice carries over.
- Settings > About and updates has Update notification (at launch, says when a new version is out) and Auto update (at launch, installs a new version and restarts; never while a game is running). Both are off by default.
- Launcher updates no longer have their own switch in Settings > Network: checking, Update notification and Auto update in About and updates are how you ask for them, with the network connection on.

## 1.0.3 (2026-11-23)
- Updates come only from Ionea's own GitHub releases (github.com/Kininn/ionea). The address is built in and shown in Settings > About and updates; it can't be changed or mistyped.
- Removed the "Update from" choice, the source folder, the repo address and the access token from Settings > About and updates. Older settings with these saved still load; the saved values are ignored.

## 1.0.2 (2026-11-23)
- First start: the Network screen starts on Done, so pressing through it leaves everything off. "Turn everything on" becomes "Turn everything off" once all is on.
- Downloading an emulator or core while Offline mode is on, or while its switch is off, now asks once and can switch it on right there, instead of the download list doing nothing.
- The "Found on this PC" title no longer disappears from the emulator search's result.
- The controls setup's "Play with" line shows a device that is both keyboard and mouse once, and shows longer names in full.
- The tutorial points at the Add tile for the first emulator (Add > Emulator), where the focus already is. When the first start already added an emulator, it goes straight to the game.
- The emulator's settings card has its Games heading back.
- Building Ionea from a copy of the source no longer changes the copy's build.txt; local builds count on in bin\build.txt.

## 1.0.1 (2026-11-23)
- Updates now come from Ionea's own GitHub releases (github.com/Kininn/ionea) without setting anything up: switch on Launcher updates in Settings > Network, then check in Settings > About and updates. The address can still be changed or cleared there.

## 1.0.0 (2026-11-23)
- Text cut short with … (a setting's name or value, a path) shows in full just above it while the mouse is on it.
- Settings: the note under Group in Task Manager no longer piles up on other Settings pages each time Library is opened.
- Streaming mode, in Settings > Look > In games: when it is on, Discord, OBS and other programs that capture a window see the game when they capture Ionea's window. Before, they could show the launcher without the game, or a black or frozen picture. It is off by default, adds a little delay, and applies from the next game started. With it on, a separate emulator's window sits inside Ionea's, so an emulator that captures the mouse for a mouse camera may not get it.
- The Home menu no longer freezes a separate emulator by default, which could upset sound or controllers in some games. An emulator whose own pause key is known is paused with it; others keep running behind the menu. Settings > Look > In games > Freeze the emulator while the menu is open brings back the old way. Games played inside the launcher pause as before.
- Ionea now comes with an installer and needs no .NET on the PC. The installer installs for you only, without administrator rights, adds a Start menu shortcut (and a desktop one if you tick it), and keeps your data and settings when you install a newer version over it. Uninstalling asks whether to remove your data too. The portable zip is still there and needs no .NET either.
- The launcher's text can now be translated: a Language setting in Settings > Look > Window (Automatic follows the Windows display language when there is a translation, else English). Only English is included for now.
- Games whose emulator draws with Vulkan can now be played inside the launcher, for example with a Vulkan renderer chosen in the emulator's settings. They need a graphics driver with Vulkan; without one, or for emulators that only draw with Direct3D, a message says so.
- Picture filters for games played inside the launcher: Sharp, Smooth, CRT (scanlines and a slight gap between pixels) and LCD grid (for handhelds). Every game's filter is in Settings > Look > In games > Game display > Picture filter; a game's Display card has its own and one for all games of its system. Automatic stays as before: sharp, or smooth for 3D games. The patterns need the game's pixels big enough on the screen and are left out below that.
- Run-ahead for games played inside the launcher: a press shows on screen 1 to 4 frames sooner. It is off by default; turn it on in Settings > Look > In games > Run-ahead. It needs a fast PC: it turns itself off for a game, with a notice, when the game's emulator can't do it or the PC can't keep up. It is not used for 3D games drawn with the GPU, and it pauses while fast-forwarding or rewinding.
- F12, or View + Y on Player 1's controller, saves a screenshot of a game played inside the launcher: the game's own picture at its own size, as a PNG in the Screenshots folder in the data folder. A small notice shows its name. The key can be changed in Settings > Input > Launcher keys.

- Games played inside the launcher can be rewound: hold F5, or View + LB on Player 1's controller, and the game runs backwards. It is on by default, kept in 128 MB of memory (64 or 256 MB can be chosen); 3D games drawn with the GPU leave it off unless Rewind in 3D games is turned on. Games whose emulator can't save their state have no rewind. The settings are in Settings > Look > In games > Fast-forward and rewind.
- Games played inside the launcher can be fast-forwarded: hold F6, or View + RB on Player 1's controller. The speed (2x to 8x, or no limit) and whether the key is held or pressed to turn it on and off are in Settings > Look > In games > Fast-forward and rewind; the key can be changed in Settings > Input > Launcher keys. The sound is off while fast, and the game's timing and sound come back as before when it ends.

- Group in Task Manager (Settings > Library > Advanced) is now off by default, also for settings that had it on: emulators start from their own exe again, so GPU profiles, Windows graphics settings and firewall rules for them match. The links made for grouping are removed. Turn it back on to group emulators in Task Manager.

## Development build 65 (2026-11-22)
- Games played inside the launcher keep time with the display when their frame rate is close to its refresh rate, so scrolling no longer stutters from a repeated frame every few seconds. On a display that is an exact multiple of the game's rate, such as 120 Hz for a 60 fps game, each frame is shown for the same number of refreshes. The game and its sound run up to 5 % faster or slower to match. Games far from the display's rate keep their own timing, as before. Settings > Look > In games > Match the display turns it off.
- Games drawn with the GPU (3DS, GameCube and Wii) no longer wait for the graphics card every frame: the picture is copied from it in the background and shown one frame later, with one copy less. Graphics drivers without OpenGL 3.2 use the old way.
- The home screen stays quick with a big library. Only the tiles on screen and just beside it are made, and their covers are read as they come into view. With 5000 games the home row shows in under a tenth of a second instead of about four, opening and closing folders no longer re-reads thousands of covers, and the launcher uses a few hundred MB of memory instead of about 3 GB. Covers already read are kept for scrolling back; when there are too many, the ones seen longest ago go first.
- When the launcher hits a problem it can't recover from, it now says so and closes cleanly, with any game stopped and settings kept, instead of carrying on half broken. Details of every problem, including ones it recovers from, go to crash.log in the data folder.

## Development build 64 (2026-11-22)
- Lichii build 386 is a known version: the launcher keeps writing its controls and turns on the mouse camera, as it did for 366.
- The mouse camera in Lichii and Eden games follows the mouse more directly: no glide after the mouse stops, and turn speed follows hand speed in a straight line up to the game's top camera speed. Each mouse look level keeps its speed.

## Development build 63 (2026-11-22)
- An emulator definition can offer a game's mod as an On/Off row on its Game settings card, such as a "60 fps patch" row (Performance) for a 60 fps mod in Lichii's mod folder. The row only shows for a game that has the mod.

## Development build 62 (2026-11-22)
- Game settings open for Switch 2 games kept as an unpacked folder (`exefs\main`). The launcher reads the game's id from `main.npdm` beside it. How the game starts is not changed.
- Lichii has new rows: Game's own DLSS, NVIDIA DLSS and FSR sharpening (Image quality), and Frame generation and Frame gen multiplier (Performance).

## Development build 61 (2026-11-22)
- The mouse camera in Lichii and Eden games turns on reliably. The launcher now waits until the game window has settled and is in front, then checks that the camera came on and tries again if it didn't.

## Development build 60 (2026-11-21)
- Lichii 366 is now a tested version: the launcher sets its controls, mouse camera and quiet settings again, as it did for 228.
- The Home menu key works in a game played inside the launcher while any window of its emulator is in front, not only its game window.

## Development build 59 (2026-11-21)
- A game's helper can ask for the emulator to start under its own program name. Some emulator add-ons are not loaded when the emulator runs under the launcher's name. That game then shows under the emulator's own name in Task Manager; it still plays inside the launcher and ends with it.

## Development build 58 (2026-11-21)
- A game can have a helper: a program that runs beside it while it plays, such as a local stand-in for a service the game asks for at start. The launcher starts it first, passes its settings on to the emulator, starts the game when the helper is ready and tells it to stop when the game closes. If the helper can't get ready, the game doesn't start and a message says why. For now a helper is set in the library file.

## Development build 57 (2026-11-21)
- Game info lists a Switch game's mods in Eden or the yuzu family under Updates and add-ons. A mod is listed by its folder name with "(mod)" after it. A mod turned off in the emulator shows "(mod, off)" in a softer colour.

## Development build 56 (2026-11-21)
- The library has a richer finish, with the same layout and nothing new to press. A soft glow in the selected game's colours sits behind the row and follows the selection. The selected tile lifts a little. Tiles have deeper shadows and a light gloss with a bright top edge. The row fades out at a screen edge where more games continue past it. The corners are slightly darker, the game's name is a little bolder, the divider fades out at its ends, and the round buttons have soft shading and a light rim. With Reduce effects on, the glow is hidden.

## Development build 55 (2026-11-20)
- The recommended keyboard and mouse controls now follow PC games: the console's main button is E, its other main button Space, the other two R and F, the shoulders Q and Shift, Start Enter, Select Tab. On consoles with a camera stick the triggers are the left and right mouse buttons, the d-pad is 1 2 3 4 and the arrow keys move the camera. PlayStation keys are unchanged. Back to the recommended controls picks up the new keys; keys you set yourself stay.
- The profile's Favourite game and the badges are gone: no Favourite row, no badge strip or Badges section on Play stats, and no Badge earned pop-ups after a game.

## Development build 54 (2026-11-20)
- The library slides in: when you reach it from the start screen, and after you add a game (or a scan finds new ones), the tiles glide in from the right one column after another while a soft band of light sweeps over them. With Reduce effects on, the slide is shorter and there is no light.

## Development build 53 (2026-11-20)
- A game in a container can go back to the home row: its Move choice (hold the game, or its game menu) now has Back to the home row first, then the other containers. Taking out the last game closes the container that is left empty.

## Development build 52 (2026-11-20)
- Hold A, Enter or the mouse button on a container, or right-click it, for its quick menu: Open, Rename and Remove container. Removing a container asks first and puts its games back on the home row. No game is deleted.

## Development build 51 (2026-11-20)
- A new cover picture opens Adjust cover straight away, and the whole picture is kept, not only its middle. You can zoom out past the tile to fit more of the picture; the gap is filled with a soft blur of it. "Whole picture" fits all of it.
- Game info > Rename, or click the name, to change a game's name.
- Delete and Backspace in the name box when adding a game edit the name instead of closing the card. Holding Backspace keeps deleting.

## Development build 50 (2026-11-20)
- A game that runs inside the launcher keeps a little more sound ready for its first 30 seconds, so the first game after the PC sat idle no longer clicks once at the start.

## Development build 49 (2026-11-20)
- A game's picture is drawn on a thread of its own, so drawing it never holds up the game or its sound.

## Development build 48 (2026-11-20)
- Games that run inside the launcher sound much sooner after what happens on screen: about 25 ms of sound waits to be played, instead of about 100 ms. The sound follows a change of the default output device, and if the low-latency output isn't available the earlier way is used.
- A press shows in the picture of a game that runs inside the launcher about 33 ms sooner: the picture goes straight to the screen while nothing of the launcher's is over it. It looks the same as before.
- An on-screen keyboard: with a controller, A on a text box (a profile name, search, a folder name) opens it. B deletes, X types a space, Y switches upper case, LB and RB move the cursor, Start is Done.
- Choosing a file or folder (a game, an emulator, a cover, a picture, a required file) opens the launcher's own browser, so it works from the couch with a controller. Drives that don't answer quickly, such as a network drive whose server is off, are left out instead of freezing the list.
- PlayStation and Nintendo controllers work in the menus and in games that run inside the launcher, with no extra program. A controller that Steam or DS4Windows also shows as an Xbox pad counts once.
- Scanning finds games in folders up to three levels deep, skips save, screenshot and media folders, and shows "only partly looked through" when a very large folder is cut short.
- Multi-disc games: a playlist (.m3u) is one game for all its discs, a cue sheet stands for its track files, and discs without a playlist keep their disc number in the name. Every disc gets the game's cover.
- A settings or library file that can't be read no longer loses your data: the launcher uses the copy from the save before, keeps the broken file next to it and says so once. Every save keeps that earlier copy.
- Moving games or saves to another drive copies and checks everything before the old files are deleted.
- Emulator downloads are checked against the size and checksum the release gives before they are used. The Azahar download points at the right file again.
- Going online: the Network page starts with Turn everything on.
- Links the launcher puts in place for a game's saves replace only links it made itself, never a real folder or file.

## Development build 42 (2026-11-20)
- The intro and outro are back to the simple ones with the icon art: her eyes and the IONEA title flicker on, she is revealed, then the start screen. The anime intro and outro are retired.

## Development build 41 (2026-11-20)
- New Brightness setting in Settings > Look and layout: turn the whole launcher down (40-100 %) for a bright display.

## Development build 40 (2026-11-20)
- The intro and outro are a few tones dimmer in light mode, a soft grey instead of bright white.

## Development build 39 (2026-11-20)
- The outro cuts straight from her open eye to her face with her eyes closed, with no zoom out.

## Development build 38 (2026-11-20)
- The outro now pulls back from her open eye to a close-up of her face with her eyes closed, instead of the far wide shot; her eye closes in place as the camera pulls back.

## Development build 37 (2026-11-20)
- The intro follows the light theme: in light mode her eyes bring the picture out of white instead of out of the dark.
- A new outro when closing: it starts on her open eye, the intro's last picture, pulls back to her lying with her eyes closed in a faint light, then fades to black (dark theme) or white (light theme).

## Development build 34 (2026-11-19)
- On/Off settings are now switches: a pill that slides on and off, instead of the words On and Off.

## Development build 33 (2026-11-19)
- The intro plays twice as fast (about 2 seconds), and the last shot, her eye, holds a little longer than the others.

## Development build 32 (2026-11-19)
- The intro is shorter (about 4 seconds) and changes shots with plain cuts instead of zooms. Her eyes are the only light: when they flicker, the light on her face flickers with them. It ends with a simple dissolve into the start screen.

## Development build 31 (2026-11-19)
- A new intro: three full-screen anime shots of Ionea - wide, her face, then her eye - joined by zoom cuts. Her eyes flicker on, and their light floods the screen into the start screen. It fills the whole screen with nothing cut off; any press skips it.

## Development build 30 (2026-11-19)
- Menus use the same words everywhere: the hold menu says Start and Put in a container like the game menu, an emulator's page has Remove from Ionea, Game controls is called that on its own page too, and Display mode starts with Same as Ionea.
- Input (on Home) has the same boxes and rows as Settings > Controls. Discord status is only in Settings > Network.
- The + tile on Home is called Add, as it adds games, emulators and containers.
- Shorter messages: error messages point to the right place in Settings, and the core offer and a few others lost their long explanations. Look for emulators says so when it finds nothing new.

## Development build 29 (2026-11-19)
- Updates from a GitHub repo: Update now downloads the release's zip itself, checks its SHA-256, installs it and restarts. Your games, settings and saves are never touched.
- A private repo works with your own access token (Settings > About and updates > Access token). It stays on this PC, encrypted, and is sent only to GitHub.
- No access to the repo, or a refused token, now says so and what to do.
- Nothing is checked or downloaded while Offline mode is on or the Launcher updates switch is off. Every request is in Settings > Network > Network activity.

## First development build (2026-11-18)
- Ionea can run from any folder: its data folder is made next to it on first start, or in your own app data folder (%LOCALAPPDATA%\Ionea\data) when the program's folder can't be written (Program Files). The installer puts it in <system drive>\Ionea\Launcher unless you pick another folder, and says so if the .NET SDK is missing.
- Which consoles get the mouse camera is now set in the definitions ("mouseCamera" in systems.json), so your own definitions can add one.
- Game settings and a core's in-game settings are sorted into boxes: Display, Image quality, Performance, Audio, Controls, Advanced. A setting with no box of its own is placed by its name; any emulator, also one added later, sorts the same way.
- Settings pages are in boxes: Look and layout (Appearance, Home screen, Window, In games, Intro and outro), Sound, Controls, Library, Network, About and updates. Reduce effects moved to Appearance, Watch the tutorial to About and updates.
- Clearer names: Theme, Sort, Screen mode, Game display, Hover sounds, Pop-up when connected, Button layout, and on an emulator's page Systems and Settings for every game. Long explanations under menus were shortened or removed.
- A fresh Ionea now behaves like one set up earlier: every emulator it finds or you add uses the controls you set up (Ionea manages controls is on; turn it off on the emulator's page), and a game you add gets its box art right away instead of at the next start.
- No built-in folders: the update source folder is empty until you set one.
- Emulators that go full screen by themselves (mGBA) now stay inside Ionea's window, also when Ionea is windowed. Before, they filled the whole monitor and the Home menu opened slowly and stretched.
- No more white bars beside the Home menu when a game runs in an Ionea window that isn't 16:9.
- Adding a standalone emulator now offers its core (mGBA, melonDS, Dolphin, Azahar), downloaded from the libretro team's builds: games then play inside Ionea and the Home menu opens at once. Cores can also be picked in Download an emulator.
- mGBA, melonDS, Azahar, Eden, Lichii, Dolphin and the yuzu and Citra families: their own "pause in the background" is turned off, as Ionea pauses the game. mGBA no longer shows its File / Edit menu bar in the Home menu.
- First start: a short tutorial (about a minute) shows how to add an emulator and a game, start it and open the Home menu.
  It points at the real buttons and moves on when you have done each step. Skip any time; play it again from Settings > About and updates > Watch the tutorial.
- Icon shape: the "+" in Who's playing? and on the Add game tile now take the chosen shape too (they stayed round).
- Home menu: "Restart the game" is there for every game, also emulators and native ports (Bloodborne, Ocarina of Time).
- Containers: the glass now matches the menus in light and dark (the menu card's white or charcoal, see-through, same corners and shadow), instead of a blue-grey tint of its own.
- Lichii and Eden: the Home menu key no longer takes a screenshot or opens the screenshot folder.
- Performance overlay: a slim bar tucked into the top of the game (or the bottom), small by default, FPS first.
  Frame rate now also for emulators and native ports inside Ionea (exactly, for a port that writes a frame log).
  Lichii: Esc no longer leaves full screen.
- Discord status: Ionea's icon stays the big picture and the game's cover is now the small one in its corner,
  for covers from Steam, LibRetro or SteamGridDB. A game played through its native PC port says PC, not its emulator.
- The performance overlay is just text on the game now, with no bar behind it.
- Ionea has a version number: shown in Settings > About and updates and small on the start screen.
- Updates: check a source folder or a GitHub repo for a newer Ionea and update by restarting. A release zip you download is used only when its SHA-256 matches.
  Your games, settings and saves (the data folder) are never touched. Go back to the previous version at any time.
- Own in-game performance overlay, display settings for games, outro when closing, cover zoom, colour presets.
