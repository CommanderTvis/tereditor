# Building

From a source checkout; [Machine setup](setup.md) lists what to install per platform and what the
repo provisions itself. macOS builds need the Xcode command-line tools and its Metal Toolchain
(`xcodebuild -downloadComponent MetalToolchain`). The first build downloads pinned
Ghostty (libghostty-vt 1.0.0), Zig 0.17.1 and Merman into machine-wide caches shared by every checkout. No installed
Ghostty application is required.

```sh
scripts/mise x -- dotnet run --project src/Tereditor.UI -c Release
```

To build the macOS app bundle (the app as a NativeAOT binary):

```sh
scripts/mise run package:macos
open artifacts/tereditor.app
```

## Run the optimised local build

```sh
scripts/mise run native
```

Publishes the desktop app as NativeAOT with `IlcInstructionSet=native` and `OptimizationPreference=Speed` into `artifacts/native` and runs it, on macOS from the bundle `artifacts/native/tereditor.app` so the Dock shows the app icon in the system's icon style (`--no-run` publishes only, `--print` shows the commands). The binary is tied to this machine's CPU and may not start on another one. The publish is slow compared with `mise run dev` and uses several GB of memory, and it does not go through `queue-heavy`; use it only for profiling or for daily use of an optimised build. Agents never run it.

## NativeAOT

The desktop app, the remote host and headless tereditor are NativeAOT binaries; the checks and the plugin runner stay JIT. Packaging and its gate are in the [scripts spec](../../scripts/SPEC.md#nativeaot-packages).

An analyzer build and NativeAOT publishes of the remote host and of the desktop app (full trimming) are the
authority. After the analyzer build was clean, the remote host publish reported one site (the IDE bridge's tool
list, fixed before the app was published), and the desktop app publish reported only reflection bindings in five
XAML data templates. Those templates were compiled afterwards; the build and `--conformance` cover them, not a
second publish. The first publish of headless tereditor (osx-arm64) reported nothing. What stood in the way, and how each went:

1. The WebSocket RPC payload codec was protobuf-net, which builds serializers at run time. Host, remote host and
   clients all used it. A codec generated at build time replaced it and keeps the wire bytes, since a remote host
   and the app ship independently.
2. `PluginJson.Options` resolves only from the generated contexts plugins declare (the Plugin API's
   [JSON metadata](../../src/Tereditor.Plugins.Api/SPEC.md#json-metadata)); its runtime-typed sites (`GetType()`, `Deserialize(type, …)`,
   `GetTypeInfo`, the tool schema) resolve through them, and the wire writes declared types. No
   `System.Text.Json` reflection is left: the host, the app and every builtin plugin serialize from
   source-generated metadata or JSON nodes.
3. The desktop app embeds the host and ASP.NET Core, so it inherits the first two. The host's endpoints are plain
   request delegates that read their route values themselves, since minimal-API delegate binding is
   reflection-based (see the [remote host](../../src/Tereditor.Host.Remote/SPEC.md)).
4. Avalonia bindings are all compiled. The kit observes properties directly instead of binding by string path,
   its control styles are a compiled XAML class instead of a style include by URI, and every data template names
   its `x:DataType`. The `AVLN3001` note on the kit's front matter editor stays: it only says the file cannot be
   loaded by URI at run time, which nothing does.
5. Smaller sites. Helper programs beside the app are found through the app's base directory, since an assembly
   has no file location in a single-file or AOT binary. The Recycle Bin on Windows uses source-generated COM
   interop. A diagnostic log names its callers through `DiagnosticMethodInfo`.

Packages, each decided from the two publishes:

- Tomlyn: our use was reflection (`Deserialize<TomlTable>`). Fixed in our use: the project's `.tereditor` files
  and Codex's `config.toml` read through a generated `TomlSerializerContext` for the untyped table, which gives
  the same model and the same JSON (`--project-toml`).
- TextMateSharp, Svg.Controls.Skia.Avalonia, Markdig, QRCoder, Avalonia with SkiaSharp and HarfBuzzSharp, and
  ASP.NET Core: no warning from any of them in either publish. Kept, with no trimmer roots and no suppressions.
- The AOT compiler package SDK 10.0.401 brought (Microsoft.DotNet.ILCompiler 10.0.12) had a high-severity
  advisory. The SDK pin moved to 10.0.403, whose compiler (10.0.14) is past it, before the first AOT package.

Every project under `src/` except the Android and Browser heads and the plugin runner (`Tereditor.Plugins.Runner`, the
one JIT process) sets `IsAotCompatible` (`Directory.Build.props`),
which runs the trim, AOT and single-file analyzers on every build, so a new IL2xxx or IL3xxx fails the build. It
also marks those assemblies trimmable, which changes neither head: Android does not trim, and the Browser build
roots every assembly it links.

The analyzers read C# only. Two kinds of code reach the AOT compiler without them:

- XAML compiled to reflection bindings. `--conformance` fails on `x:CompileBindings="False"` or
  `ReflectionBinding` anywhere under `src/`.
- A collection expression whose target type picks a generic `Add<T>` that needs dynamic code. A `JsonArray`
  built from `JsonObject` elements did. Only a NativeAOT publish finds this; every package build runs one, so CI's
  macOS, Linux and Windows package jobs do.

The browser build's hybrid AOT is no evidence either way: it silences trim warnings and keeps the interpreter as a
fallback. A `pureWeb` host has no external plugins anyway.

## Instrumented local build

```sh
scripts/mise run instrumented-native [--no-run] [--print] [--malloc-stack-logging]
```

Same as `native`, for looking into the memory of the running app. It publishes into `artifacts/instrumented-native` (on macOS the bundle `tereditor.app` there) with the same app identifier, so settings and state stay shared. It keeps the symbol table in the binary, writes `tereditor.dSYM` beside the bundle on macOS, and links the EventPipe runtime so the diagnostics socket exists. `--malloc-stack-logging` starts the app with `MallocStackLogging=1`, which costs memory and speed, so leave it off unless you hunt small native allocations. The app strips `MallocStackLogging` and the other `MallocStack*` variables from the environment of its terminals and every other process it starts, so those processes print no stack-logging notices and run at normal speed. Agents never run it.

Attach from another terminal. Install the tools once into a private path:

```sh
dotnet tool install dotnet-counters --tool-path ~/.tereditor/diag-tools
dotnet tool install dotnet-trace --tool-path ~/.tereditor/diag-tools
dotnet tool install dotnet-gcdump --tool-path ~/.tereditor/diag-tools
```

The app opens a socket named `dotnet-diagnostic-<pid>-<n>-socket` in `$TMPDIR`. `dotnet-counters`, `dotnet-trace` and `dotnet-gcdump` all attach to the NativeAOT binary through it (checked on the instrumented build). Find it with `ls "$TMPDIR" | grep dotnet-diagnostic`, or list the processes with `~/.tereditor/diag-tools/dotnet-counters ps`, which shows the app. Then:

```sh
~/.tereditor/diag-tools/dotnet-counters monitor --process-id <pid> System.Runtime
~/.tereditor/diag-tools/dotnet-trace collect --process-id <pid> --providers Microsoft-Windows-DotNETRuntime:0x200001:5
~/.tereditor/diag-tools/dotnet-gcdump collect --process-id <pid> -o heap.gcdump
```

The trace provider line samples allocations by type (`GCSampledObjectAllocationHigh`) and logs GCs. `dotnet-gcdump` asks the runtime for a heap dump through EventPipe. It works on the NativeAOT binary but gives only the total heap bytes and object counts, with no type names. `dotnet-counters collect --process-id <pid> --duration 00:00:10 --format json -o counters.json` records counters for a fixed time. To see exceptions, trace the runtime exception keyword: `dotnet-trace collect --process-id <pid> --duration 00:00:10 --providers Microsoft-Windows-DotNETRuntime:0x8000:5`. For the native side:

```sh
footprint <pid>        # dirty memory by category
vmmap -summary <pid>   # regions: GC heap, malloc, graphics
heap <pid> -sortBySize # malloc blocks by size
leaks <pid>            # unreferenced blocks; with --malloc-stack-logging it shows their stacks
malloc_history <pid> -callTree -allBySize
```

Limits: `dotnet-dump` does not work on NativeAOT, because there is no CoreCLR dump support to read. There is no per-type live-object histogram: for types, use the allocation sampling from `dotnet-trace`. The instrumented build is its own publish with its own output folder.

## Memory

A build target that runs a file-based script from a project directory must use `dotnet run --file <script>`. `dotnet run <script>` in a directory with a `.csproj` passes the script to the project as an argument, so it builds and runs that project, whose build runs the same target again: a chain of nested `dotnet` processes that grows until memory is gone. The Browser project's asset staging, bundle compression and preserved-assembly check use `--file` for this reason.

Expected size of a Browser build: the compile and the non-AOT bundle stay near 2 GB; the Release AOT link (wasm-ld, then wasm-opt) is the largest step and is the one place a Browser build goes above 6 GB. Run every heavy command through `queue-heavy`; if one is killed at its ceiling, look for a recursive process chain before raising the ceiling.
