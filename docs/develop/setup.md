# Development machine setup

What a checkout provisions itself, and what you install. Run every task through `scripts/mise run <task>`
(`scripts\mise.cmd` on Windows); see [scripts/README.md](../../scripts/README.md).

## Provisioned by the repo

mise can be the one on your machine or one the repo provisions:

- System mise: `scripts/mise` runs one found on PATH, in Homebrew or in `~/.local/bin` as is, with your own mise
  configuration. It must meet the `min_version` in `mise.toml`.
- Auto-provisioned mise: without one, `scripts/mise` downloads the pinned, checksum-verified release into mise's data
  directory, as CI does.

Either way there is no bootstrap step. mise installs the tools below from `mise*.toml` (versions and SHA-256 per
platform in the matching `mise*.lock`) on first use, into its data directory (default `~/.local/share/mise`), shared by
every checkout and worktree of the user. `scripts/tool <name>` prints where a pinned tool lives, installing it if
missing; tasks run under `scripts/mise` find `dotnet` and the others on PATH.

| Tool (`scripts/tool` name) | Pinned in | Used by |
| --- | --- | --- |
| .NET SDK (`dotnet`), Zig 0.17.1 (`zig-0.17.1`; Windows: `zig-windows`), Merman (`merman`, macOS), Temurin JDK 21 (`jdk`; not Windows, where `JAVA_HOME` names a JDK) | `mise.toml` | every build, check and package task |
| Android NDK r27d (`android-ndk`), Merman's Android libraries (`merman-android`) | `mise.android.toml` | `android`, `package:android` |
| Node, pnpm, Binaryen, PDFium WASM (`node`, `pnpm`, `binaryen`, `pdfium-wasm`) | `mise.browser.toml` | `browser`, `package:browser` |
| appimagetool and the AppImage runtime (`appimagetool`, `appimage-runtime`) | `mise.toml` | `package:linux` |

The build recipes also fetch, pinned by commit or checksum, the Ghostty sources and Zig dependency mirror
(`RestoreZigDeps` in `scripts/lib/Native.cs`) and Go for the Android Tailscale bridge (`scripts/tailscale-go.cs`). Build caches and those
downloads live in the machine-wide `TEREDITOR_CACHE_DIR` (`~/.cache/tereditor`), shared by every checkout; see
[scripts/README.md](../../scripts/README.md). NuGet packages restore from nuget.org in locked mode. The app downloads
managed language servers at runtime, not the build.

## Install yourself

### All platforms

- Git 2.36 or newer on `PATH`: the build fetches pinned sources with it, and the app needs `git worktree list -z`
  (see [docs/linux.md](../linux.md)).

### macOS (`build`, `dev`, `package:macos`, `check:terminal`)

- `bash` and `curl` (system): `scripts/mise-pinned` downloads mise with them when none is installed.
- Xcode command-line tools (`xcode-select --install`): `xcrun clang` builds the Merman and notification libraries and
  links the NativeAOT app and host in `package:macos`,
  `xcrun --sdk macosx` supplies Zig's SDK (`scripts/build-ghostty-macos.cs`), and `codesign` and `hdiutil` sign
  the bundle and make the `dmg`.
- The Metal toolchain (`xcodebuild -downloadComponent MetalToolchain`): libghostty compiles its Metal shaders.

### Linux (`build`, `package:linux`, running the AppImage)

- `bash`, `curl`, `tar`, POSIX `sh`, `patch` and `shasum`: the mise download (and `patch` for the native recipes).
- To run the app: glibc 2.35 or newer (Ubuntu 22.04 or newer), an X11 or XWayland session, `libX11`, `libXcursor`, `libXrandr`, `libXi`,
  `fontconfig` and a GL or software Skia stack ([docs/linux.md](../linux.md)).
- Pack the AppImage on a host of the target architecture (`TEREDITOR_APPDIR_ONLY=1` assembles the AppDir anywhere).
  The NativeAOT client links with the system clang (`clang` and `zlib1g-dev` on Ubuntu), so a Linux packaging host needs them.

### Windows (`build`, `package:windows`)

- Windows 10 1809 or later or Windows 11, x64 or ARM64; Git for Windows, Windows PowerShell and `tar.exe`
  ([docs/windows.md](../windows.md)). Zig compiles the native libraries (`scripts/build-ghostty-windows.cs`).
- `package:windows` only: Visual Studio 2022 or its Build Tools with the C++ build tools for the target
  architecture (`Microsoft.VisualStudio.Component.VC.Tools.x86.x64` or `.ARM64`) and a Windows SDK; NativeAOT links
  the client and host with them. `build` and `dev` do not need them.
- Android builds need macOS or Linux (the NDK pin has no Windows archives).

### Android (`android`, `package:android`; macOS or Linux)

- Nothing to install: the Android SDK, JDK, NDK and Merman's prebuilt `.so` come from mise, and the .NET `android`
  workload installs into the .NET SDK on first use.
- The Android SDK is pinned in `mise.android.toml` (`scripts/tool android-sdk`). On first use the task installs
  `platforms;android-36`, `build-tools;36.0.0` and `platform-tools` into it. It records the accepted SDK license from a
  pinned hash in `scripts/tasks/android.cs`, so it never asks you to accept it.
- Set `ANDROID_HOME` only to build with another SDK. The task then uses it as it is and installs nothing. Set
  `JAVA_HOME` only to build with another JDK 17 or 21.
- A device or emulator for `android run`.
