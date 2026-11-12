# Dependency overlays

Release compatibility uses the pinned `yy-thunks` 1.2.3 source port, revision 1.
It verifies the official source archive SHA-512 and applies the reviewed ordinal and hardened
build patches. The upstream BuildObj/FixObj path builds one XP-targeted object per architecture
with `/W4 /GS /Qspectre`, using an explicitly installed LibMaker from `PATH`. The port never
acquires developer tools, invokes machine integration scripts or installs the optional analyzer.
Only `share/yy-thunks/objs/<arch>/YY_Thunks_for_WinXP.obj` and the MIT copyright notice are installed.
Release uses the ordinary static MSVC Spectre CRT; VC-LTL is not a dependency. The packaged notice
is retained in `licenses/YY-Thunks.txt`.

## Godot decoder overlays

The decoder ports build the exact single-header snapshots qualified for the Godot adapters. Each port
downloads only its pinned header files, checks their SHA-512 hashes, builds a static library for the
selected vcpkg triplet and installs a copyright notice identical to the one packaged with Godot.
The header SHA-256 values below identify the selected upstream bytes; changing a
revision requires a deliberate compatibility review and new codec tests.

| Port | Source revision | Installed header SHA-256 |
|------|-----------------|-------------------------|
| bcdec 0.985 | [iOrange/bcdec `80859ed3b7afb1c527a2a99d70c61457bea72d0c`](https://github.com/iOrange/bcdec/tree/80859ed3b7afb1c527a2a99d70c61457bea72d0c) | `bcdec.h` `134520764d96f70a27db814173616c89ad85db95fbd3328417c7a8c77e7ca189` |
| drlibs (`dr_mp3` 0.7.5, `dr_wav` 0.14.7 snapshot) | [mackron/dr_libs `f12e197fd462e4c22873fbd70d11b1d2f2c1dc3c`](https://github.com/mackron/dr_libs/tree/f12e197fd462e4c22873fbd70d11b1d2f2c1dc3c) | `dr_mp3.h` `7a6cc38b0afdb55cfc9eec991d9169d67beb8fd910be2995121510cdaa99c356`; `dr_wav.h` `7c9660d75361ad6175e6217c63a1075c9512445c06bc951197fc479179f76b39` |
| qoa | [Godot 4.7.2-stable `ed1daf0bf001b61586d9930840f2f1394092c079`](https://github.com/godotengine/godot/tree/ed1daf0bf001b61586d9930840f2f1394092c079) `thirdparty/misc/qoa.h` | `qoa.h` `ab501e91f4afd18c811e689713e5fd3956030562bf80cda784d304af17133f01` |

The QOA port deliberately uses Godot's fork rather than the current phoboslab header. The `drlibs`
port overrides the built-in vcpkg port because this project tested a later exact revision. The Godot
package's notices remain in `licenses/bcdec.txt`, `licenses/dr_libs.txt` and `licenses/qoa.txt`.
The QOA header selected from Godot 4.7.2-stable is byte-identical to the previously tested
4.5-stable header (23,845 bytes); its download hashes, license and decoder behavior are unchanged.

The drlibs 2026-11-11 commit is an exact source snapshot: its headers label dr_mp3 0.7.5
as unreleased and dr_wav 0.14.7 as pending. Both headers and their static library are rebuilt
together; the Godot first-frame MP3 probe and PCM16 WAV writer keep their existing contracts.

Brotli 1.2.1 comes from a package-specific Git registry in `vcpkg-configuration.json`.
Both its baseline and reference select the official vcpkg commit
`61d7eaeb094a6acbc23d9c2e54de9208175fda47`; all other packages keep the manifest's
builtin baseline. This reuses the official port without maintaining another overlay copy.
The build's source attestations include the registry configuration and the installed SPDX
records retain the actual recipe tree and upstream source checksum. Task-local restores
set `X_VCPKG_REGISTRIES_CACHE` to an existing directory inside the project alongside their
download and binary caches.

Catch2 3.16.1 and Mbed TLS 4.1.1 LTS are also pinned here while the registry trails these upstream
releases. Catch2 retains the registry's static-library and manual-main packaging. The Mbed TLS port
uses the complete official release archive, including TF-PSA-Crypto 1.1.1 and generated sources.
Godot links the static crypto provider through its project-owned PSA adapter; TLS and X.509 are not
linked into the module. Renovate follows the 4.1 LTS patch series, supported through March 2029.
