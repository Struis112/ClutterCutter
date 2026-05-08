# ClutterCutter

A lightweight Windows disk-usage browser built and maintained by **Struis ICT**.

Single-file native `.exe` (~70 KB), no installer, no dependencies beyond the .NET Framework 4 runtime that already ships with every modern Windows.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-FFDD00?style=flat&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/struis112)

If ClutterCutter saves you time hunting down what's eating your disk, consider [buying me a coffee ☕](https://buymeacoffee.com/struis112) — it keeps this and other Struis ICT tools coming.

## Features

- **Drive picker** with live used/free bars — click any drive to scan it.
- **Tree + list view** sorted by size; column-click to sort, double-click to drill in, breadcrumb to jump back.
- **"Top Largest Folders"** view that flattens the entire subtree to a sorted list of biggest space hogs.
- **MFT fast path** — when scanning an NTFS drive root as Administrator, ClutterCutter reads the Master File Table directly via `\\.\C:` and parses the records. ~5–10× faster than walking the file system; full C: drive (~1 M files / 100 GB) in ~6 seconds.
- **FindFirstFileEx fallback** with parallel top-level fan-out and `LARGE_FETCH` for non-admin / subfolder scans.
- **Always-visible % progress** in the bottom-left status bar.
- **Dark / Light / Auto theme** that follows the Windows system theme by default. Title bar, scroll bars, and progress bar all themed via the documented + undocumented uxtheme/dwmapi APIs.
- **Admin elevation prompt** at startup if not elevated, with one-click UAC relaunch.
- **Right-click actions:** Open in Explorer, Copy path, Open Command Prompt here, Properties, Recycle.

## Download

Pre-built binaries are attached to each [GitHub Release](https://github.com/Struis112/ClutterCutter/releases). Just download `ClutterCutter.exe` and run it — it's a single self-contained file.

## Building from source

You only need a Windows machine. The .NET Framework 4 C# compiler ships with Windows; no Visual Studio or .NET SDK install required.

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
    -nologo -target:winexe -optimize+ -platform:anycpu `
    -reference:System.Windows.Forms.dll `
    -reference:System.Drawing.dll `
    -reference:System.dll `
    -reference:System.Core.dll `
    -reference:Microsoft.VisualBasic.dll `
    -win32icon:ClutterCutter.ico `
    -out:ClutterCutter.exe `
    ClutterCutter.cs
```

GitHub Actions reproduces this same build on every push (`.github/workflows/build.yml`).

## Releasing

Releases are managed by [release-please](https://github.com/googleapis/release-please). Conventional-commit messages on `main` keep an open "release PR" up to date with the next version + a generated changelog. Merging that PR creates the tag and the GitHub Release.

Because tags created by `GITHUB_TOKEN` don't recursively trigger workflows, the **Build** workflow has a `workflow_dispatch` trigger so we can run it manually for a published tag — go to **Actions → Build → Run workflow**, enter the tag (e.g. `v0.2.0`), and the exe is built and attached to the matching Release. (For fully automated tag → exe upload, swap the release-please action's token for a PAT secret.)

## Keyboard shortcuts

| Key             | Action                                  |
|-----------------|-----------------------------------------|
| `F5`            | Refresh / re-scan                       |
| `Esc`           | Stop the running scan                   |
| `Backspace`     | Go to parent folder (when tree focused) |
| `Enter`         | Drill into the selected list row        |
| `Del`           | Move selected items to Recycle Bin      |

## Notes on file counts

When MFT mode is active, hard-linked files (which `WinSxS` uses heavily) are counted **once** by their canonical name — that's why the file/folder totals can differ from a tree-walking scanner that counts each path separately. The MFT total reflects what's actually on disk.

## Repo

Pushed and maintained at [Struis112/ClutterCutter](https://github.com/Struis112/ClutterCutter).

---

© Struis ICT — all rights reserved.
