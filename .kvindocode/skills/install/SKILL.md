---
name: install
description: Build KvindoCode for Linux/macOS/Windows, publish the binaries as a GitHub release, and install the app on the user's PC from that release. Use whenever the user says "build a release", "publish binaries", "make a mac/windows build", "upload a new version to GitHub", "install kvindocode", "update kvindocode from the releases page", or asks how KvindoCode is distributed.
---

# Building, publishing and installing KvindoCode

KvindoCode is a .NET 9 / Avalonia desktop app. One script builds all three platforms, one script installs the result.

| Script | What it does |
|---|---|
| `tools/release.sh <version>` | Publishes all platforms and creates the GitHub release with the archives attached |
| `install.sh` | Installs on this machine — from a checkout, or from the GitHub release |

Repo: `Kvindo/KvindoCode` (change with `KVINDOCODE_REPO=owner/name`).

## 1. Publish a release

```bash
bash tools/release.sh 2026.10.10                     # build all platforms + create the release
bash tools/release.sh 2026.10.10 --draft             # create it as a draft to check first
bash tools/release.sh 2026.10.10 --notes "Changelog"  # override the generated release notes
bash tools/release.sh 2026.10.10 --no-push           # build the files only, into ./release/<version>/
bash tools/release.sh 2026.10.10 --framework-dependent  # small archives, needs .NET 9 on the user's machine
```

Self-contained is the default, so a user needs **no .NET installed**: download, unpack, run.

Assets produced in `release/<version>/` and uploaded:

| Archive | Platform |
|---|---|
| `kvindocode-<v>-linux-x64.tar.gz` | Linux x86-64 |
| `kvindocode-<v>-osx-arm64.tar.gz` | macOS Apple silicon |
| `kvindocode-<v>-osx-x64.tar.gz` | macOS Intel |
| `kvindocode-<v>-win-x64.zip` | Windows x86-64 |
| `icon.png` | app icon (install.sh fetches it as an asset) |

Each is ~42–45 MB self-contained; a full four-platform run takes under a minute for an incremental build.

**Cross-building works from Linux** for every RID (verified 2026-10-10) — you do not need a Mac or a Windows box.
macOS/Windows archives are **not code-signed**, so the user gets Gatekeeper/SmartScreen warnings on first run
(right-click → Open on macOS, "More info → Run anyway" on Windows); say so rather than letting it look broken.

Always run the tests first, and only release a committed tree so the archives match the tag:

```bash
dotnet test tests/KvindoCode.Tests          # must be green
git status --short                          # must be empty
bash tools/release.sh <version>
```

## 2. Install on this PC from the GitHub release

```bash
bash install.sh --from-release            # newest release
bash install.sh --from-release 2026.10.10 # a specific version
bash install.sh                           # no argument: build from the checkout and install
```

It detects the platform (`uname -s` / `uname -m`), downloads the matching asset with `gh release download`, unpacks
it, and installs. On a machine without `gh`, download the archive by hand from the releases page and unpack it over
`~/.local/share/kvindocode`.

Install locations (override the root with `KVINDOCODE_PREFIX`):

| Path | What |
|---|---|
| `~/.local/share/kvindocode/` | the published app (`kvindocode` apphost + `kvindocode.dll`) |
| `~/.local/bin/kvindocode` | launcher — `kvindocode [dir]`, `kvindocode --last`, `kvindocode -p "text"` |
| `~/.local/share/applications/kvindocode.desktop` | desktop entry |
| `~/.local/share/icons/hicolor/512x512/apps/kvindocode.png` | icon |

## 3. The install rule that must never be broken

**Never `rm -rf` or bulk-copy over `~/.local/share/kvindocode` while the app is running.** A running KvindoCode holds
open handles to the files it started with; replacing the directory out from under it killed the user's live instance
(2026-10-04). The install therefore swaps **per file** — `mv -f` over each path is a rename, so the running process
keeps its own inodes and only picks the new build up when it is restarted.

Two related traps, both already handled in `install.sh` — keep them that way:

* The "remove files the new build no longer has" step must compare against a **snapshot of the incoming file list
  taken before the swap**. After `mv` the staging directory is empty, so comparing against it would make every
  installed file look stale and delete the whole install.
* `set -e` + `[ -f x ] && …` aborts the script when the test is false (the expression returns 1). Use an `if`.

## 4. Do not hammer the machine while building

A naive `dotnet publish` saturates a 16-core box and makes the desktop unresponsive — and the user has hit this.
`tools/_build.sh` is sourced by both scripts and applies three independent limits:

| Knob | Default | Meaning |
|---|---|---|
| `KVINDOCODE_BUILD_JOBS` | `1` | MSBuild nodes (`-maxcpucount:N`); `0` = one per core |
| `KVINDOCODE_BUILD_NICE` | `10` | `nice` level, plus `ionice -c2 -n7` when available |
| the `flock` | always on | one build at a time (`~/.kvindocode-build/dotnet-build.lock`) |

`KVINDOCODE_BUILD_FAST=1` lifts both limits for a one-off fast build.

Measured on this 16-core box (clean rebuild of the Linux build, load average ~11): the throttled build finished in
**9.5 s wall / 21.8 s CPU**, the unthrottled one in **16.8 s wall / 29.3 s CPU** — on a busy machine the gentle build
is *both* faster and lighter, because full parallelism mostly fights the load already there. Do not "optimise" it away.

If a build seems stuck, check `flock -n ~/.kvindocode-build/dotnet-build.lock true` — a stale MSBuild node holding
the lock is why `_build.sh` closes the lock fd inside a subshell and passes `-nodeReuse:false`.

## 5. Verifying an install (do this — do not assume)

```bash
D=~/.local/share/kvindocode
"$D/kvindocode" --print "say hi"                                    # the apphost runs
find src -name '*.cs' -newer "$D/kvindocode.dll" -not -path '*/obj/*' | wc -l   # 0 = nothing is staler
kvindocode --last                                                   # the launcher works from PATH
```

Installing does not restart the app: tell the user a restart is needed, and that background tasks and loops are not
autocontinued, rather than killing their window.

## 6. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `gh: command not found` | `gh` is needed for `--from-release` and for publishing; install it or download the archive by hand |
| `No prebuilt archive for <os> / <arch>` | unusual platform (musl, armv7, 32-bit Windows) — build from a checkout on that machine |
| App does not start after install | self-contained build missing its runtime → reinstall from the archive (unpack the whole directory, not just the apphost) |
| `zip` errors on the Windows asset | `apt install zip` |
| Release created but a platform asset 404s, or the assets show 0 bytes | a failed/partial upload — check your storage quota and re-run; the user has hit this |
| Both scripts wait forever | another build holds the flock: see `~/.kvindocode-build/dotnet-build.lock` |
