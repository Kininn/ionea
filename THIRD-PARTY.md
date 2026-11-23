# Third-party data and components in Ionea

Ionea ships no emulators, cores, firmware, BIOS files, keys, game files, DLC or updates.

The files in `definitions\` describe emulators and systems. Some of them are built from the
data sources below. Only data is copied; no program code is copied.

| Source | Licence | Version used | What Ionea uses |
|---|---|---|---|
| Hand-written by the Ionea project | (Ionea's own) | | `definitions\systems.json` entries without a `source` field (the project's own systems), `definitions\emulators\*.json` and `definitions\cores\*.json` |
| [Playnite](https://github.com/JosefNemec/Playnite) emulator definitions (`source/Playnite/Emulation`) | MIT | commit e2e6efcda311fea38fcd92f61c8ca90811d97f28 | `definitions\emulators\playnite\`: emulator names, program-name patterns, command lines, platforms and file types |
| [ES-DE](https://gitlab.com/es-de/emulationstation-de) `resources/systems/windows/es_systems.xml` | MIT | commit ddc2653b26be233daf24a06f629017da6452560b | `definitions\systems.json` entries with `"source": "es-de"`: system names, full names and file types; which libretro core each system uses |
| [libretro-core-info](https://github.com/libretro/libretro-core-info) | MIT | commit eac3a2784d428444aa2bd472039b5c5540141af1 | `definitions\cores\libretro\`: core names, file types, firmware file names, paths and MD5 hashes (no firmware itself) |

Every imported file names its source in its `source` field.

The firmware MD5s in the hand-written `definitions\cores\mgba-core.json`, `melondsds-core.json` and
`definitions\emulators\duckstation.json` (`requiredFiles`) are copied from libretro-core-info (same commit).

Ionea does not ship libretro-database's `System.dat` (CC-BY-SA). If the user puts their own copy in
`<data>\definitions\`, Ionea reads it on their PC to check BIOS files; nothing from it is copied into Ionea.

## .NET

`Launcher.exe` is published self-contained: it carries the .NET 10 runtime and the Windows Desktop runtime (WPF)
inside it, so the PC it runs on needs no .NET install. Both are Microsoft's .NET, released under the MIT licence
(below). Their own third-party notices are in
[dotnet/runtime](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT) and
[dotnet/wpf](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT).

## Licences

### Playnite

```
MIT License

Copyright (c) 2020 Josef Nemec

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### ES-DE

```
Copyright (c) 2024-2026 Northwestern Software AB
Copyright (c) 2020-2024 Leon Styhre
Copyright (c) 2014 Alec Lofquist

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
```

### libretro-core-info

```
MIT License

Copyright (c) 2019 The RetroArch team

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### .NET

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
