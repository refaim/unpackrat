# Repository agent instructions

## Build

This project has a console-first Windows build based directly on MSBuild and pinned manifest-mode vcpkg dependencies.
Repository CMake is not part of the build; CMake use inside a vcpkg port is acceptable.

The build tooling is a single plain Python driver: `build/main.py` owns the command contract, the vcpkg restore,
the MSBuild calls and the packaging, and `build/leaks.py` is the standalone UMDH worker. There is no dependency graph,
content-addressed store, scheduler, report normaliser or template renderer to keep in sync; MSBuild owns project
evaluation, dependencies and incremental state under the stable `out/native` tree, and each CI operation runs one real
tool whose exit code is the verdict.

Use the root entry point from an ordinary PowerShell or `cmd.exe` console:

```powershell
.\build.ps1 build -Arch x86,x64 -Config Release
.\build.ps1 test -Arch x86,x64 -Config Debug
.\build.ps1 ci-source
.\build.ps1 ci-arch -Arch x64
.\build.ps1 package
```

Run the relevant build, tests, and checks after changing code. Do not install or update developer tools automatically;
report missing prerequisites to the user. Release modules must remain MSVC-built, `/MT`, self-contained binaries for
x86 and x64 with no third-party runtime DLLs.

All production changes follow strict TDD: state the observable requirement or invariant, add a focused test that fails
for the expected reason, implement the smallest correct change, and refactor only while the suite remains green. Every
bug fix starts with a regression test. The required coverage gate is 100% first-party source lines and branches; do not
weaken thresholds, exclude production files, write coverage-only tests with no behavioral assertion, or add broad
suppressions to make a check pass.

## Development guidelines

This project uses C++23 and follows the high-assurance engineering policy in
`docs/critical-software-methodology.md`. In particular:

- Preserve clean dependency direction: platform/Observer adapters depend on application and parser code, never the
  reverse. Format parsing belongs in a platform-neutral core and must not acquire Observer or Win32 dependencies.
- Isolate every third-party library used by production code behind a project-owned adapter, whether that library is
  written in C or C++. External headers, types, error codes, and allocator or ownership contracts stay inside the
  adapter implementation; our interfaces expose only our own types and errors, explicit input/output bounds, and
  values or borrowed ranges. Test-only libraries and frameworks, and unmodified vendored code, are exempt. Prefer
  caller-provided buffers and our allocator hooks where the library supports them; a library-created context or buffer
  is owned immediately by our RAII wrapper together with the library's matching release function. Raw ownership leaks,
  mismatched allocators, exceptions escaping C callbacks, and manual naked lifetime management in application code stay
  prohibited, and this isolation is our ownership and control contract rather than a claim that every library permits
  replacing its internal allocator. The existing first-party zlib wrapper is one local example, not evidence that all
  current dependencies already comply, and it does not justify blanket wrappers around test-only Catch2, JSON, or
  xxHash use.
- Keep the C ABI boundary strict. Export only C-compatible, fixed-layout data and explicit sizes. Never let STL types,
  C++ exceptions, allocator ownership, or implicit lifetime assumptions cross the boundary. Validate all inbound
  pointers and structure sizes, initialize outputs defensively, and translate every internal failure to the documented
  ABI result at the outermost boundary.
- Use value semantics and RAII for every resource, including memory, files, module handles, and temporary output.
  Application C++ must not contain owning `new`, `delete`, `malloc`, `calloc`, `realloc`, or `free`. A raw pointer or
  reference is non-owning; prefer references, `std::span`, and `std::string_view` where they express the contract.
  Prefer `std::unique_ptr` for polymorphic ownership. `std::shared_ptr` requires a documented, genuinely shared
  lifetime; use `std::weak_ptr` to break cycles.
- Treat archive bytes, metadata, paths, counts, offsets, sizes, and callback behavior as untrusted input. Validate
  before use, use checked arithmetic before narrowing/allocation/seeking, impose explicit resource and iteration
  bounds, guarantee loop progress, and avoid input-driven recursion unless a strict depth limit is enforced.
- Express security-relevant condition combinations as executable, data-driven decision-table tests.
- Keep functions cohesive, control flow reviewable, ownership explicit, and preprocessor use minimal. Avoid magic
  numbers, hidden global state, duplicated policy, and speculative abstraction. KISS and DRY remain subordinate to
  clear boundaries and independently testable behavior.
- A check suppression or deviation must be narrow, explained beside the code or in the decision log, and reviewed by
  the owner. Never catch `std::bad_alloc`, `std::length_error`, access violations, or sanitizer findings merely to make
  fuzzing or tests pass.

## Architecture overview

unpackrat is a collection of archive format implementations for exotic archives. Each implementation covers one
format family and is exposed through an adapter; the current adapter is an Observer plugin module for FAR Manager that
implements the Observer API.

### Core components

- **API layer** (`src/api.h`, `src/dll.cpp`): Observer entry points such as `OpenStorage`, `CloseStorage`, `GetItem`,
  and `ExtractItem`.
- **Archive wrapper** (`src/archive.h`, `src/archive.cpp`): common archive lifecycle and extraction behavior.
- **Extractor interface** (`src/modules/extractor.h`): the internal contract implemented by each format module.

### Module structure

Supported modules live under `src/modules/`:

- `renpy/`: Ren'Py RPA archives and their Pickle index parser;
- `rpgmaker/`: RPG Maker VX Ace RGSS3A archives;
- `zanzarah/`: Zanzarah PAK archives.

Each contains format-specific implementation, a `.def` export definition, and `observer_user.ini` registration data.

### Data flow

1. FAR Manager/Observer loads the module through `LoadSubModule()`.
2. `OpenStorage()` creates an archive wrapper with the format extractor.
3. `PrepareFiles()` validates and indexes archive contents.
4. `GetItem()` exposes entry metadata.
5. `ExtractItem()` streams an entry to the requested destination with progress/cancellation reporting.

### Tests

Catch2 tests live in `src/tests/`. Unit tests exercise parser logic directly, while integration and ABI contract tests
load the actual module binaries without requiring FAR Manager. Small repository-owned fixtures are mandatory. The
external golden corpus is an optional compatibility/stress layer selected with `-Corpus`.

See `docs/build-system.md` for the current command contract and build architecture.

## Windows XP SP3 verification VM

XP portability checks run against a reusable Windows XP x86 SP3 guest, never a disposable one. Docker runs inside
Ubuntu WSL2; there is no native Windows docker CLI, so Docker goes through WSL. The guest uses TCG software emulation
(`KVM=N`) and is reached from the host at `http://localhost:8006`.

Canonical VM directory: `C:\Users\Roma\.codex\vm\unpackrat-xp-sp3-x86` (in WSL,
`/mnt/c/Users/Roma/.codex/vm/unpackrat-xp-sp3-x86`). Only its `shared` subfolder is shared: it is a host bind mounted
into the container as `/shared`, which the guest reaches as `\\host.lan\Data`. The canonical root itself is not shared,
and `/shared` is a container path, not an XP mount. The permanent container/EXTERNAL volume is
`unpackrat-unity-xp-sp3-x86-20261017`.

Start only the guarded `compose.yml` in the canonical directory. It pins the `dockurr/windows` image by digest
(`sha256:59f8bed19cfd49cf1701debe34f9df6311e61a4e2ae68aa9f41206e2184e31cc`) and mounts a read-only
`install.boot-only.sh` whose `startInstall` refuses the stock automatic installation and cleanup; that guard constrains
only this entry point, so tools run manually could still change the disk. Inspect the compose, confirm the volume
exists, and confirm the readonly guard is present before starting:

```powershell
wsl -d Ubuntu -- docker compose -f /mnt/c/Users/Roma/.codex/vm/unpackrat-xp-sp3-x86/compose.yml up -d --no-build --pull never
```

Reuse requires durable preconditions to already exist: the installed disk, the readonly guard, and a verified
consistent backup. If the disk, volume, or metadata is missing, stop and investigate; never reinitialize or reinstall
automatically. Never start the old unguarded container `unpackrat-xp-sp3-x86-smoke` or an old
`xp-x86-sp3-smoke/compose.yml`, where a lost marker lets the stock installer auto-delete `data.img` before erroring. Do
not assume a plain `docker start` is safe for a stock dockurr image, do not remove the guard or boot media/state on a
guessed interpretation, and shut the guest down cleanly from inside XP before stopping the container.

Before any migration, recreation, image change, or storage or boot configuration change, shut the guest down cleanly
and keep a verified consistent backup outside every Docker volume, in
`C:\Users\Roma\.codex\vm\unpackrat-xp-sp3-x86\backups`; restore into a separate volume and leave the original
untouched. Routine operation (booting, logs, copying files) needs no backup. Never run `docker compose down -v`,
`docker volume rm`, `docker volume prune`, or `docker system prune --volumes` against the permanent VM, and never
rename, delete, or clean up its disk, state, or backups; deleting or replacing the VM, disk, or backups requires
Roma's explicit instruction, and routine cleanup never does them. Do not offer destructive convenience commands.
Report on-guest behavior only from inside the running guest: host and static checks do not prove guest execution, and
never state portability results that were not observed.
