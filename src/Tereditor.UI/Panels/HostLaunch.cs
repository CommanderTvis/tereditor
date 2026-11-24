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
    public static IReadOnlyList<LaunchOption> All { get; } =
    [
        new("windows-script", HostOs.Windows, "PowerShell script",
            "Run this in PowerShell, as a normal user, in the folder of your project. It downloads the Windows build from the nightly release, checks it against SHA256SUMS, and starts the host there.",
            """
            $ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
            $me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
            if ($me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this as a normal user, not as administrator.' }
            $arch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
            $name = "tereditor-nightly-win-$arch.zip"
            $base = 'https://github.com/CommanderTvis/tereditor/releases/download/nightly'
            $dir = Join-Path $env:LOCALAPPDATA 'tereditor-host'
            New-Item -ItemType Directory -Force $dir | Out-Null
            Invoke-WebRequest "$base/$name" -OutFile "$dir\$name"
            Invoke-WebRequest "$base/SHA256SUMS" -OutFile "$dir\SHA256SUMS"
            $want = (Get-Content "$dir\SHA256SUMS" | Where-Object { $_ -match ('  ' + [regex]::Escape($name) + '$') }) -replace '\s.*$', ''
            if (-not $want -or (Get-FileHash "$dir\$name" -Algorithm SHA256).Hash -ne $want) { throw 'The download does not match SHA256SUMS. Not running it.' }
            Expand-Archive "$dir\$name" "$dir\files" -Force
            $env:TEREDITOR_ROOT = (Get-Location).Path
            & "$dir\files\host\tereditor-host.exe"
            """),
        new("macos-script", HostOs.MacOs, "Shell script",
            "Run this in Terminal, as a normal user, in the folder of your project. It downloads the nightly disk image, checks it against SHA256SUMS, and runs the app's host without a window. It needs an Apple silicon Mac.",
            """
            set -eu
            [ "$(id -u)" -ne 0 ] || { echo 'Run this as a normal user, not as root.' >&2; exit 1; }
            base=https://github.com/CommanderTvis/tereditor/releases/download/nightly
            dir=$(mktemp -d)
            trap 'hdiutil detach -quiet "$dir/mnt" 2>/dev/null || true; rm -rf "$dir"' EXIT
            curl -fsSL -o "$dir/tereditor-nightly.dmg" "$base/tereditor-nightly.dmg"
            curl -fsSL -o "$dir/SHA256SUMS" "$base/SHA256SUMS"
            (cd "$dir" && grep ' tereditor-nightly.dmg$' SHA256SUMS | shasum -a 256 -c -)
            hdiutil attach -nobrowse -readonly -quiet -mountpoint "$dir/mnt" "$dir/tereditor-nightly.dmg"
            TEREDITOR_ROOT="$PWD" "$dir/mnt/tereditor.app/Contents/MacOS/tereditor" --host-only
            """),
        new("linux-script", HostOs.Linux, "Shell script",
            "Run this in a terminal, as a normal user, in the folder of your project. It downloads the nightly AppImage for your processor, checks it against SHA256SUMS, and runs its host without a window.",
            """
            set -eu
            [ "$(id -u)" -ne 0 ] || { echo 'Run this as a normal user, not as root.' >&2; exit 1; }
            case "$(uname -m)" in x86_64) arch=x64 ;; aarch64 | arm64) arch=arm64 ;; *) echo 'No build for this processor.' >&2; exit 1 ;; esac
            name="tereditor-nightly-linux-$arch.AppImage"
            base=https://github.com/CommanderTvis/tereditor/releases/download/nightly
            dir="$HOME/.local/share/tereditor-host"
            mkdir -p "$dir"
            curl -fsSL -o "$dir/$name" "$base/$name"
            curl -fsSL -o "$dir/SHA256SUMS" "$base/SHA256SUMS"
            (cd "$dir" && grep " $name\$" SHA256SUMS | sha256sum -c -)
            chmod +x "$dir/$name"
            TEREDITOR_ROOT="$PWD" "$dir/$name" --appimage-extract-and-run --host-only
            """),
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