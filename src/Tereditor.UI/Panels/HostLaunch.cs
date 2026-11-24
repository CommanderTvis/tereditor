namespace Tereditor.UI.Panels;

public enum HostOs { Windows, MacOs, Linux, Other }

/// <summary>One way to start a host: some words, and the text to copy when there is any.</summary>
public sealed record LaunchOption(string Id, HostOs Os, string Title, string Steps, string Command);

/// <summary>
/// What the browser's connect screen offers for starting a host, in the order that suits the visitor's system. Only things the
/// nightly release really publishes appear here; a row for a new asset is one more entry in <see cref="All"/>.
/// </summary>
public static class HostLaunch
{

    /// <summary>
    /// Reads the system from the browser's own words: <c>navigator.userAgentData.platform</c> or <c>navigator.platform</c>, then the
    /// user agent. Phones and tablets are <see cref="HostOs.Other"/>, since they cannot run a host.
    /// </summary>
    public static HostOs Detect(string? platform, string? userAgent)
    {
        var text = ((platform ?? "") + " " + (userAgent ?? "")).ToLowerInvariant();
        if (text.Contains("android") || text.Contains("iphone") || text.Contains("ipad") || text.Contains("cros")) return HostOs.Other;
        if (text.Contains("win")) return HostOs.Windows;
        if (text.Contains("mac")) return HostOs.MacOs;
        if (text.Contains("linux") || text.Contains("x11")) return HostOs.Linux;
        return HostOs.Other;
    }

    /// <summary>The options for a system, its own first and the rest after.</summary>
    public static (IReadOnlyList<LaunchOption> First, IReadOnlyList<LaunchOption> Other) Order(HostOs os) =>
        ([.. All.Where(option => option.Os == os)], [.. All.Where(option => option.Os != os)]);

    // The Windows archive holds host/tereditor-host.exe beside the desktop client. SHA256SUMS lists every release file. The host
    // prints its address and a one-time pairing code; the page asks for that code, so no token is copied.
    // Every script keeps all it creates in one per-user folder with a manifest whose first line is the marker. The uninstall scripts
    // read that manifest and delete only the paths it records inside the folder. The folder and the marker are shared constants.
    public const string Marker = "tereditor-host-install v1";
    public const string UnixDirectory = "$HOME/.tereditor/host-install";
    public const string WindowsDirectory = @"%LOCALAPPDATA%\tereditor-host";

    private const string UnixDirLine = "dir=\"" + UnixDirectory + "\"";
    private const string UnixGuard = """
        set -eu
        [ "$(id -u)" -ne 0 ] || { echo 'Run this as a normal user, not as root.' >&2; exit 1; }
        [ -n "${HOME:-}" ] && [ "$HOME" != / ] || { echo 'HOME is not set.' >&2; exit 1; }
        """;

    public static IReadOnlyList<LaunchOption> All { get; } =
    [
        new("windows-script", HostOs.Windows, "PowerShell script",
            "Run this in PowerShell, as a normal user, in the folder of your project. It downloads the Windows build from the nightly release, checks it against SHA256SUMS, and starts the host there. It installs only into " + WindowsDirectory + " and writes manifest.txt there. It adds no service, no PATH entry and no registry key.",
            $$"""
            $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
            $me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
            if ($me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this as a normal user, not as administrator.' }
            $arch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
            $name = "tereditor-nightly-win-$arch.zip"
            $base = 'https://github.com/CommanderTvis/tereditor/releases/download/nightly'
            $dir = Join-Path $env:LOCALAPPDATA 'tereditor-host'
            if (Test-Path -LiteralPath $dir) { throw "Already installed in $dir. Run the uninstall command first." }
            $state = Join-Path $env:USERPROFILE '.tereditor\host'
            New-Item -ItemType Directory $dir | Out-Null
            $manifest = Join-Path $dir 'manifest.txt'
            Set-Content -LiteralPath $manifest -Encoding utf8 -Value @('{{Marker}}', "source=$base/$name", 'release=nightly', "date=$((Get-Date).ToUniversalTime().ToString('o'))", "state=$state", "path=$dir\$name", "path=$dir\SHA256SUMS", "path=$dir\files", "path=$dir\host.pid")
            Invoke-WebRequest "$base/$name" -OutFile "$dir\$name"
            Invoke-WebRequest "$base/SHA256SUMS" -OutFile "$dir\SHA256SUMS"
            $want = (Get-Content "$dir\SHA256SUMS" | Where-Object { $_ -match ('  ' + [regex]::Escape($name) + '$') }) -replace '\s.*$', ''
            if (-not $want -or (Get-FileHash "$dir\$name" -Algorithm SHA256).Hash -ne $want) { throw 'The download does not match SHA256SUMS. Not running it.' }
            Add-Content -LiteralPath $manifest -Encoding utf8 -Value "sha256=$want"
            Expand-Archive "$dir\$name" "$dir\files"
            Remove-Item -LiteralPath "$dir\$name"
            $env:TEREDITOR_ROOT = (Get-Location).Path
            $run = Start-Process "$dir\files\host\tereditor-host.exe" -WorkingDirectory $env:TEREDITOR_ROOT -NoNewWindow -PassThru
            Set-Content -LiteralPath "$dir\host.pid" -Value $run.Id
            $run.WaitForExit()
            """),
        new("macos-script", HostOs.MacOs, "Shell script",
            "Run this in Terminal, as a normal user, in the folder of your project. It downloads the nightly disk image, checks it against SHA256SUMS, copies the app into " + UnixDirectory + ", unmounts the image, and runs the app's host without a window. It needs an Apple silicon Mac. It writes manifest.txt there and changes nothing else.",
            $$"""
            {{UnixGuard}}
            name=tereditor-nightly.dmg
            base=https://github.com/CommanderTvis/tereditor/releases/download/nightly
            {{UnixDirLine}}
            [ ! -e "$dir" ] || { echo "Already installed in $dir. Run the uninstall command first." >&2; exit 1; }
            mkdir -p "$dir"
            printf '%s\n' '{{Marker}}' "source=$base/$name" release=nightly "date=$(date -u +%Y-%m-%dT%H:%M:%SZ)" "state=$HOME/.tereditor/host" "path=$dir/$name" "path=$dir/SHA256SUMS" "path=$dir/mnt" "path=$dir/tereditor.app" "path=$dir/host.pid" > "$dir/manifest.txt"
            trap 'hdiutil detach -quiet "$dir/mnt" 2>/dev/null || true' EXIT
            curl -fsSL -o "$dir/$name" "$base/$name"
            curl -fsSL -o "$dir/SHA256SUMS" "$base/SHA256SUMS"
            (cd "$dir" && grep " $name\$" SHA256SUMS | shasum -a 256 -c -)
            echo "sha256=$(grep " $name\$" "$dir/SHA256SUMS" | cut -d' ' -f1)" >> "$dir/manifest.txt"
            hdiutil attach -nobrowse -readonly -quiet -mountpoint "$dir/mnt" "$dir/$name"
            ditto "$dir/mnt/tereditor.app" "$dir/tereditor.app"
            hdiutil detach -quiet "$dir/mnt"
            rm -f -- "$dir/$name"
            TEREDITOR_ROOT="$PWD" sh -c 'echo $$ >"$0"; exec "$@"' "$dir/host.pid" "$dir/tereditor.app/Contents/MacOS/tereditor" --host-only
            """),
        new("linux-script", HostOs.Linux, "Shell script",
            "Run this in a terminal, as a normal user, in the folder of your project. It downloads the nightly AppImage for your processor, checks it against SHA256SUMS, unpacks it into " + UnixDirectory + ", and runs its host without a window. It writes manifest.txt there and changes nothing else.",
            $$"""
            {{UnixGuard}}
            case "$(uname -m)" in x86_64) arch=x64 ;; aarch64 | arm64) arch=arm64 ;; *) echo 'No build for this processor.' >&2; exit 1 ;; esac
            name="tereditor-nightly-linux-$arch.AppImage"
            base=https://github.com/CommanderTvis/tereditor/releases/download/nightly
            {{UnixDirLine}}
            [ ! -e "$dir" ] || { echo "Already installed in $dir. Run the uninstall command first." >&2; exit 1; }
            mkdir -p "$dir"
            printf '%s\n' '{{Marker}}' "source=$base/$name" release=nightly "date=$(date -u +%Y-%m-%dT%H:%M:%SZ)" "state=$HOME/.tereditor/host" "path=$dir/$name" "path=$dir/SHA256SUMS" "path=$dir/squashfs-root" "path=$dir/host.pid" > "$dir/manifest.txt"
            curl -fsSL -o "$dir/$name" "$base/$name"
            curl -fsSL -o "$dir/SHA256SUMS" "$base/SHA256SUMS"
            (cd "$dir" && grep " $name\$" SHA256SUMS | sha256sum -c -)
            echo "sha256=$(grep " $name\$" "$dir/SHA256SUMS" | cut -d' ' -f1)" >> "$dir/manifest.txt"
            chmod +x "$dir/$name"
            (cd "$dir" && "./$name" --appimage-extract > /dev/null)
            rm -f -- "$dir/$name"
            TEREDITOR_ROOT="$PWD" sh -c 'echo $$ >"$0"; exec "$@"' "$dir/host.pid" "$dir/squashfs-root/AppRun" --host-only
            """),
    ];

    private const string UnixUninstallSteps =
        "Run this in a terminal to remove the host that the start command above installed. It stops that host if it still runs (only the process recorded in the install folder), " +
        "removes " + UnixDirectory + " and everything its manifest.txt lists, and prints what it removed. It refuses to touch a folder without the manifest marker, and it is safe to run twice. " +
        "It keeps your own settings and projects in ~/.tereditor/host. Paired browser grants and pairing codes live only in the host's memory, so stopping it ends them. " +
        "To delete ~/.tereditor/host as well, change purge=0 to purge=1 on the second line first.";

    // The same script serves macOS and Linux: it detaches a macOS volume left by an interrupted install when hdiutil exists.
    private const string UnixUninstall = $$"""
        set -eu
        purge=0
        [ -n "${HOME:-}" ] && [ "$HOME" != / ] || { echo 'HOME is not set.' >&2; exit 1; }
        {{UnixDirLine}}
        [ -e "$dir" ] || { echo "Nothing to uninstall: $dir does not exist."; exit 0; }
        [ -d "$dir" ] && [ ! -L "$dir" ] && [ "$(head -n 1 "$dir/manifest.txt" 2>/dev/null)" = '{{Marker}}' ] || { echo "Refusing: $dir has no tereditor manifest marker. Nothing was removed." >&2; exit 1; }
        pid=$(head -n 1 "$dir/host.pid" 2>/dev/null || true)
        case "$pid" in '' | *[!0-9]*) ;; *)
          if ps -p "$pid" -o command= 2>/dev/null | grep -qF -- "$dir/"; then
            pkill -P "$pid" 2>/dev/null || true
            kill "$pid" 2>/dev/null || true
            echo "Stopped the host (process $pid)."
          fi ;;
        esac
        if command -v hdiutil > /dev/null 2>&1 && [ -d "$dir/mnt" ]; then hdiutil detach -quiet "$dir/mnt" 2>/dev/null || true; fi
        state=$(sed -n 's/^state=//p' "$dir/manifest.txt" | head -n 1)
        while IFS= read -r line; do
          case "$line" in path=*)
            p=${line#path=}
            case "$p" in
              *..*) echo "Skipped $p (not a plain path)." ;;
              "$dir"/?*) if [ -e "$p" ] || [ -L "$p" ]; then rm -rf -- "$p" && echo "Removed $p"; fi ;;
              *) echo "Skipped $p (outside the install folder)." ;;
            esac ;;
          esac
        done < "$dir/manifest.txt"
        if [ "$purge" = 1 ]; then
          case "$state" in
            "$HOME"/.tereditor/?*) if [ -e "$state" ]; then rm -rf -- "$state" && echo "Removed $state (host settings and projects)."; fi ;;
            *) echo "Skipped state folder '$state' (not under ~/.tereditor)." ;;
          esac
        else
          echo "Kept $state (your settings and projects)."
        fi
        rm -f -- "$dir/manifest.txt"
        if rmdir "$dir" 2>/dev/null; then echo "Removed $dir"; else echo "Left $dir: it holds files this install did not record."; fi
        """;

    private const string WindowsUninstallSteps =
        "Run this in PowerShell to remove the host that the start command above installed. It stops that host if it still runs (only the process recorded in the install folder), " +
        "removes " + WindowsDirectory + " and everything its manifest.txt lists, and prints what it removed. It refuses to touch a folder without the manifest marker, and it is safe to run twice. " +
        "It keeps your own settings and projects in .tereditor\\host in your user folder. Paired browser grants and pairing codes live only in the host's memory, so stopping it ends them. " +
        "To delete that folder as well, change $purge = $false to $purge = $true on the second line first.";

    private const string WindowsUninstall = $$"""
        & {
        $ErrorActionPreference = 'Stop'; $purge = $false
        $dir = Join-Path $env:LOCALAPPDATA 'tereditor-host'
        $manifest = Join-Path $dir 'manifest.txt'
        if (-not (Test-Path -LiteralPath $dir)) { "Nothing to uninstall: $dir does not exist."; return }
        $first = if (Test-Path -LiteralPath $manifest) { Get-Content -LiteralPath $manifest -TotalCount 1 }
        if ((Get-Item -LiteralPath $dir -Force).LinkType -or $first -ne '{{Marker}}') { throw "Refusing: $dir has no tereditor manifest marker. Nothing was removed." }
        $lines = Get-Content -LiteralPath $manifest
        $pidFile = Join-Path $dir 'host.pid'
        $id = if (Test-Path -LiteralPath $pidFile) { (Get-Content -LiteralPath $pidFile -TotalCount 1) }
        if ("$id" -match '^\d+$') {
            $proc = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
            if ($proc -and $proc.Path -and $proc.Path.StartsWith("$dir\", [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $proc.Id -Force; $proc.WaitForExit(5000) | Out-Null; "Stopped the host (process $id)."
            }
        }
        $root = [IO.Path]::GetFullPath($dir).TrimEnd('\') + '\'
        foreach ($line in $lines) {
            if (-not $line.StartsWith('path=')) { continue }
            $p = [IO.Path]::GetFullPath($line.Substring(5))
            if ($p.Length -le $root.Length -or -not $p.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { "Skipped $p (outside the install folder)."; continue }
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; "Removed $p" }
        }
        $state = ($lines | Where-Object { $_.StartsWith('state=') } | Select-Object -First 1)
        $state = if ($state) { $state.Substring(6) }
        $home2 = Join-Path $env:USERPROFILE '.tereditor\'
        if ($purge -and $state -and [IO.Path]::GetFullPath($state).StartsWith($home2, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $state)) { Remove-Item -LiteralPath $state -Recurse -Force; "Removed $state (host settings and projects)." }
        else { "Kept $state (your settings and projects)." }
        Remove-Item -LiteralPath $manifest -Force
        if (@(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0) { Remove-Item -LiteralPath $dir; "Removed $dir" } else { "Left $dir: it holds files this install did not record." }
        }
        """;

    /// <summary>The commands that remove what the start commands installed, each for one system.</summary>
    public static IReadOnlyList<LaunchOption> Uninstall { get; } =
    [
        new("windows-uninstall", HostOs.Windows, "Uninstall the host started from this page", WindowsUninstallSteps, WindowsUninstall),
        new("macos-uninstall", HostOs.MacOs, "Uninstall the host started from this page", UnixUninstallSteps, UnixUninstall),
        new("linux-uninstall", HostOs.Linux, "Uninstall the host started from this page", UnixUninstallSteps, UnixUninstall),
    ];

    /// <summary>What the visitor must allow, and what to do after a refusal, in plain words.</summary>
    public const string LocalNetworkNotice =
        "This page must be allowed to reach your own computer. Chrome, Edge and Brave ask first: \"Local network access\" or \"Access other apps and services on this device\". Click Allow. " +
        "If you blocked it, click the icon at the left of the address bar, open Site settings, set that permission to Allow, and reload this page. " +
        "Safari and Firefox may not ask. If the connection fails there, turn off content blockers or extensions for this site, and check that the host is running.";

    /// <summary>Why the code is safe to type, and what it does.</summary>
    public const string PairingNotice =
        "The host prints its address and a pairing code. Enter both below and leave the token blank. The code works once and expires in five minutes. No long-lived secret is copied.";
}