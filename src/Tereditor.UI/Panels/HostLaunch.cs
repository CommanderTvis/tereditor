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
    private const string Releases = "https://github.com/CommanderTvis/tereditor/releases/tag/nightly";

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
        new("macos-app", HostOs.MacOs, "The desktop app",
            "No standalone host download exists for macOS yet. Download tereditor-nightly.dmg from the nightly release, open the app, and turn on the listener in Settings > Host. This page then asks the app to approve it.",
            Releases),
        new("linux-app", HostOs.Linux, "The desktop app",
            "No standalone host download exists for Linux yet. Download tereditor-nightly-linux-x64.AppImage (or the arm64 file) from the nightly release, start it, and turn on the listener in Settings > Host. This page then asks the app to approve it.",
            Releases),
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