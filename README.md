# unpackrat

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![CI](https://github.com/refaim/unpackrat/actions/workflows/main.yml/badge.svg)](https://github.com/refaim/unpackrat/actions/workflows/main.yml)

unpackrat brings support for exotic archives to FAR Manager. It ships as a set of Observer plugin
modules, one per archive format, that present an archive as an ordinary folder you can browse and
extract from. Each module is a single self-contained DLL with its dependencies built in and no
external runtime DLLs to install.

## Download

Install [FAR Manager](https://farmanager.com/download.php?l=en) and the
[Observer](https://github.com/lazyhamster/Observer/releases) plugin host first; unpackrat adds archive
support on top of them. Take the module ZIP whose architecture matches your FAR Manager and Observer
build (x86 or x64), not the architecture of your operating system. Choose the newest published release
from the module's release listing below.

| Module | Formats | Release | ChangeLog |
|--------|---------|---------|-----------|
| Ren'Py | `.rpa` (RPA-1.0 with adjacent `.rpi`, RPA-2.0, RPA-3.0) | [renpy](https://github.com/refaim/unpackrat/releases?q=renpy) | [RU](src/modules/renpy/ChangeLog.ru.txt) [EN](src/modules/renpy/ChangeLog.en.txt) |
| RPG Maker | `.rgssad` (XP, RGSS1), `.rgss2a` (VX, RGSS2), `.rgss3a` (VX Ace, RGSS3) | [rpgmaker](https://github.com/refaim/unpackrat/releases?q=rpgmaker) | [RU](src/modules/rpgmaker/ChangeLog.ru.txt) [EN](src/modules/rpgmaker/ChangeLog.en.txt) |
| Zanzarah | `.pak` | [zanzarah](https://github.com/refaim/unpackrat/releases?q=zanzarah) | [RU](src/modules/zanzarah/ChangeLog.ru.txt) [EN](src/modules/zanzarah/ChangeLog.en.txt) |
| Godot | `.pck` | [godot](https://github.com/refaim/unpackrat/releases?q=godot) | [RU](src/modules/godot/ChangeLog.ru.txt) [EN](src/modules/godot/ChangeLog.en.txt) |
| WolfRPG | `.wolf`, `.wolfx` | [wolfrpg](https://github.com/refaim/unpackrat/releases?q=wolfrpg) | [RU](src/modules/wolfrpg/ChangeLog.ru.txt) [EN](src/modules/wolfrpg/ChangeLog.en.txt) |
| Unity | Unity serialized files and asset bundles (e.g. `.assets`, `.bundle`, `.unity3d`) | [unity](https://github.com/refaim/unpackrat/releases?q=unity) | [RU](src/modules/unity/ChangeLog.ru.txt) [EN](src/modules/unity/ChangeLog.en.txt) |

Unity support is a published module. Open one selected Unity `.assets`, `.bundle`
or `data.unity3d` file; UnityFS internal bundles are handled automatically. Sibling `resS` and
resource files, and the other dependencies the selected file references, are resolved for that
selected file, while resources owned by unrelated dependencies are not added, so keep the sidecar
files next to the selected file. Whole-game catalogues and code decompilation are not offered.

Each entry converts on demand to its own output file. `Texture2D` and `Sprite` become PNG; `Sprite`
crops its atlas region and handles packing, flips, 90-degree rectangles, a transparent canvas and a
tight mask. `TextAsset` keeps its original bytes as `.bin`, which does not mean plain text only.
`AudioClip` uses the mature Fmod5Sharp decoder: FSB5 single-sample PCM and ADPCM export to WAV and
Vorbis to Ogg, legacy stored sound bytes are preserved, and unsupported FSB5 banks or codecs keep
their raw `.fsb`. Embedded fonts export to TTF/OTF/TTC, `VideoClip` and `MovieTexture` keep their
stored container (OGV for `MovieTexture`), and `Mesh` geometry becomes GLB, without a reconstructed
scene, materials or animations.

Original hierarchy paths are kept when present, with literal `{Type}` folders as a fallback.
DisplayedSize and PackedSize report the original source payload length (0 means unknown); converted
output may differ. An unsupported downscale value beyond 0 or 1, a tight mask that cannot be read,
or a source geometry budget refusal fails that one entry's export, so not every variant or Unity
version is guaranteed. External code, `MonoBehaviour`, `ScriptableObject`, `Material` and
`AnimationClip` are outside this resource feature. The Unity module is a single DLL with no
separate .NET runtime to install.

Each module ZIP carries the module DLL for one architecture, its `observer_user.ini`, both
human-written change logs and a generated `LICENSES.txt` at the archive root.

## Install

Unpack the ZIP for the architecture that matches your FAR Manager and Observer build into
`%FARHOME%\Plugins\Observer\modules\`, where `%FARHOME%` is your FAR Manager installation folder. If
you already have modules there, merge the archive's `observer_user.ini` with the existing file so
every module stays registered, then restart FAR Manager. After that, open a supported archive in FAR
and press Enter or Ctrl+PgDn: Observer shows its contents as a regular folder you can browse and
extract from with the usual FAR commands.

## License

unpackrat's own source is released under the [MIT License](LICENSE.txt).


## Building

The build produces self-contained `/MT` MSVC modules for x86 and x64 with no external runtime
DLLs to ship. The full command contract, prerequisites and quality gates live in
[`docs/build-system.md`](docs/build-system.md); the common entry points are:

```powershell
.\build.ps1 build -Arch x86,x64 -Config Release
.\build.ps1 test -Arch x86,x64 -Config Debug
.\build.ps1 ci-source
.\build.ps1 ci-arch -Arch x64
.\build.ps1 package
```

## Links

[Support Forum (in Russian)](https://forum.farmanager.com/viewtopic.php?t=12729)
