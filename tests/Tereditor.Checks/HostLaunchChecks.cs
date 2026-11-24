using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

using Tereditor.UI.Panels;

namespace Tereditor.Checks;

/// <summary>The browser's host commands: system detection, the order of the options, the Copy buttons and the local-access notice.</summary>
internal static class HostLaunchChecks
{
    internal static void RunUi()
    {
        Require(HostLaunch.Detect("Windows", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)") == HostOs.Windows, "Windows is detected from userAgentData.");
        Require(HostLaunch.Detect("Win32", "") == HostOs.Windows, "Windows is detected from navigator.platform.");
        Require(HostLaunch.Detect("macOS", "") == HostOs.MacOs && HostLaunch.Detect("MacIntel", "") == HostOs.MacOs, "macOS is detected.");
        Require(HostLaunch.Detect("Linux x86_64", "Mozilla/5.0 (X11; Linux x86_64)") == HostOs.Linux, "Linux is detected.");
        Require(HostLaunch.Detect("", "Mozilla/5.0 (X11; Ubuntu; Linux aarch64)") == HostOs.Linux, "Linux is detected from the user agent alone.");
        Require(HostLaunch.Detect("Linux armv8l", "Mozilla/5.0 (Linux; Android 15; Pixel 8)") == HostOs.Other, "Android is not Linux.");
        Require(HostLaunch.Detect("iPhone", "") == HostOs.Other && HostLaunch.Detect(null, null) == HostOs.Other, "Phones and the unknown get no system of their own.");

        foreach (var os in new[] { HostOs.Windows, HostOs.MacOs, HostOs.Linux, HostOs.Other })
        {
            var (first, other) = HostLaunch.Order(os);
            Require(first.All(option => option.Os == os) && other.All(option => option.Os != os) && first.Count + other.Count == HostLaunch.All.Count, $"{os}: its own options come first, the rest follow, none is lost.");
        }
        Require(HostLaunch.Order(HostOs.Windows).First.Single().Id == "windows-script" && HostLaunch.Order(HostOs.MacOs).Other.Any(option => option.Id == "windows-script"), "Windows gets the script first; macOS finds it under the others.");

        // The script names only files the nightly workflow uploads, checks the sum, and refuses administrators.
        var script = HostLaunch.All.Single(option => option.Id == "windows-script").Command;
        Require(script.Contains("tereditor-nightly-win-$arch.zip") && script.Contains("SHA256SUMS") && script.Contains("Get-FileHash") && script.Contains("IsInRole") && script.Contains("host\\tereditor-host.exe"), "The script must verify the checksum, refuse administrators and run the packaged host.");
        Require(!script.Contains("TEREDITOR_TOKEN") && !script.Contains("Invoke-Expression") && !script.Contains("iex"), "The script must neither handle a token nor run downloaded text.");
        foreach (var id in new[] { "macos-script", "linux-script" })
        {
            var unix = HostLaunch.All.Single(option => option.Id == id).Command;
            Require(unix.Contains("--host-only") && unix.Contains("SHA256SUMS") && unix.Contains("id -u") && !unix.Contains("sudo") && !unix.Contains("| sh ") && !unix.Contains("| sh\n") && !unix.Contains("| bash") && !unix.Contains("TEREDITOR_TOKEN"), id + " must check the sum, refuse root and start the host without a window.");
        }
        CheckUninstall(script);
        var workflow = File.ReadAllText(Path.Combine(FindRepo(), ".github", "workflows", "nightly.yml"));
        foreach (var asset in new[] { "tereditor-nightly-linux-x64.AppImage", "tereditor-nightly-linux-arm64.AppImage", "tereditor-nightly-win-x64.zip", "tereditor-nightly-win-arm64.zip", "SHA256SUMS", "tereditor-nightly.dmg", "tereditor-nightly-linux-x64.AppImage" })
            Require(workflow.Contains(asset), "The nightly workflow must publish " + asset);
        Require(HostLaunch.LocalNetworkNotice.Contains("Local network access") && HostLaunch.LocalNetworkNotice.Contains("Access other apps and services on this device") && HostLaunch.LocalNetworkNotice.Contains("Safari") && HostLaunch.LocalNetworkNotice.Contains("Firefox"), "The notice must name the browser prompts.");

        // The view: the visitor's system first, the rest under Other options, a Copy button per command.
        var copied = new List<string>();
        var view = new HostLaunchView(HostOs.MacOs, text => { copied.Add(text); return Task.FromResult(true); });
        var window = new Window { Width = 400, Height = 900, Content = view };
        window.Show();
        try
        {
            var all = view.GetLogicalDescendants().OfType<Control>().ToArray();
            Control Named(string name) => all.FirstOrDefault(control => control.Name == name) ?? throw new InvalidOperationException("The view lacks " + name);
            var firstPanel = (Panel)Named("LaunchFirst");
            var otherPanel = (Panel)Named("LaunchOther");
            Require(((Panel)Named("LaunchUninstall")).Children.Select(card => card.Name).SequenceEqual(["Launch_macos-uninstall"]) && Named("LaunchUninstallHeading").IsVisible, "macOS: its uninstall command sits under the start command.");
            var uninstallCopy = (Button)Named("LaunchCopy_macos-uninstall");
            uninstallCopy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(copied.SequenceEqual([HostLaunch.Uninstall.Single(option => option.Id == "macos-uninstall").Command]), "Copy puts the exact uninstall command on the clipboard.");
            copied.Clear();
            Require(firstPanel.Children.Single().Name == "Launch_macos-script" && otherPanel.Children.Select(card => card.Name).SequenceEqual(["Launch_windows-script", "Launch_linux-script", "Launch_windows-uninstall", "Launch_linux-uninstall"]), "macOS: its option first, the others under Other options.");
            Require(((TextBlock)Named("LaunchNotice")).Text == HostLaunch.LocalNetworkNotice && ((TextBlock)Named("LaunchPairing")).Text == HostLaunch.PairingNotice, "The local-access and pairing notices are on screen.");
            var copy = (Button)Named("LaunchCopy_windows-script");
            var confirmation = (TextBlock)Named("LaunchCopied_windows-script");
            Require(!confirmation.IsVisible, "Nothing is confirmed before a click.");
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(copied.SequenceEqual([script]) && confirmation.IsVisible && confirmation.Text == "Copied", "Copy puts the exact command on the clipboard and says so.");
            var failing = new HostLaunchView(HostOs.Windows, _ => Task.FromResult(false));
            window.Content = failing;
            var failed = failing.GetLogicalDescendants().OfType<Control>().ToArray();
            ((Button)failed.First(control => control.Name == "LaunchCopy_windows-script")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var message = (TextBlock)failed.First(control => control.Name == "LaunchCopied_windows-script");
            Require(message.IsVisible && message.Text!.StartsWith("Copy failed"), "A refused clipboard is reported, not hidden.");
            Require(((Panel)failed.First(control => control.Name == "LaunchFirst")).Children.Single().Name == "Launch_windows-script", "Windows: the script comes first.");
            Require(((Panel)failed.First(control => control.Name == "LaunchUninstall")).Children.Single().Name == "Launch_windows-uninstall", "Windows: its uninstall command is shown.");
            var phone = new HostLaunchView(HostOs.Other, _ => Task.FromResult(true));
            window.Content = phone;
            var phoneAll = phone.GetLogicalDescendants().OfType<Control>().ToArray();
            Require(HostLaunch.Uninstall.All(option => phoneAll.Any(control => control.Name == "LaunchCopy_" + option.Id)), "A visitor with no known system still finds every uninstall command.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS host launch: system detection, option order, release asset names, Copy buttons and the local-access notice");
    }

    // The scripts are checked as text, parsed with sh -n where a shell exists, and the shell uninstall runs against a fake install in a temp HOME.
    private static void CheckUninstall(string windowsInstall)
    {
        var windowsUninstall = HostLaunch.Uninstall.Single(option => option.Id == "windows-uninstall").Command;
        var unixUninstall = HostLaunch.Uninstall.Single(option => option.Id == "macos-uninstall").Command;
        Require(HostLaunch.Uninstall.Single(option => option.Id == "linux-uninstall").Command == unixUninstall, "macOS and Linux share one uninstall script.");
        var dirLine = "dir=\"" + HostLaunch.UnixDirectory + "\"";
        foreach (var id in new[] { "macos-script", "linux-script" })
        {
            var install = HostLaunch.All.Single(option => option.Id == id).Command;
            Require(install.Contains(dirLine) && unixUninstall.Contains(dirLine), id + ": install and uninstall use the same folder.");
            Require(install.Contains("'" + HostLaunch.Marker + "'") && unixUninstall.Contains("'" + HostLaunch.Marker + "'"), id + ": install writes and uninstall checks the same marker.");
            Require(install.Contains("\"$dir/manifest.txt\"") && !install.Contains("mktemp") && !install.Contains("$PWD/") && !install.Contains("--appimage-extract-and-run"), id + ": it installs only into its folder and records a manifest.");
        }
        Require(windowsInstall.Contains("Join-Path $env:LOCALAPPDATA 'tereditor-host'") && windowsUninstall.Contains("Join-Path $env:LOCALAPPDATA 'tereditor-host'"), "Windows: install and uninstall use the same folder.");
        Require(windowsInstall.Contains("'" + HostLaunch.Marker + "'") && windowsUninstall.Contains("'" + HostLaunch.Marker + "'"), "Windows: install writes and uninstall checks the same marker.");
        Require(!windowsInstall.Contains("-Force") && !windowsInstall.Contains("New-Service") && !windowsInstall.Contains("HKCU") && !windowsInstall.Contains("SetEnvironmentVariable") && !windowsInstall.Contains("Get-Location).Path\n&"), "The Windows install makes no system-wide change.");
        Require(windowsUninstall.Contains("-LiteralPath") && windowsUninstall.Contains("StartsWith($root") && windowsUninstall.Contains("$proc.Path.StartsWith(\"$dir\\\"") && !windowsUninstall.Contains("Stop-Process -Name") && !windowsUninstall.Contains("Get-Process -Name"), "The Windows uninstall checks the marker, stays inside the folder and stops only the recorded process.");
        foreach (var line in windowsUninstall.Split('\n').Where(line => line.Contains("Remove-Item")))
            Require(line.Contains("-LiteralPath"), "Remove-Item must use -LiteralPath: " + line.Trim());
        Require(!unixUninstall.Contains("pkill -f") && !unixUninstall.Contains("killall") && !unixUninstall.Contains("kill -9") && !unixUninstall.Contains("sudo"), "The uninstall kills no process it did not record.");
        foreach (var line in (unixUninstall + "\n" + HostLaunch.All.Single(option => option.Id == "macos-script").Command).Split('\n').Where(line => line.Contains("rm -") || line.Contains("rmdir")))
        {
            // Outside double and single quotes no dollar sign may remain, and the path must not start with a variable.
            var bare = System.Text.RegularExpressions.Regex.Replace(line, "\"[^\"]*\"|'[^']*'", "");
            Require(!bare.Contains('$'), "An rm command expands a variable without quotes: " + line.Trim());
        }

        var shell = File.Exists("/bin/sh") && !OperatingSystem.IsWindows();
        var scratch = Path.Combine(Path.GetTempPath(), "tereditor-uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            if (!shell) { Console.WriteLine("SKIP host launch shell checks: no /bin/sh here"); return; }
            foreach (var id in new[] { "macos-script", "linux-script" })
            {
                var parsed = Sh(scratch, HostLaunch.All.Single(option => option.Id == id).Command, parseOnly: true);
                Require(parsed.Code == 0, id + " does not parse with sh -n: " + parsed.Output);
            }
            Require(Sh(scratch, unixUninstall, parseOnly: true).Code == 0, "The uninstall does not parse with sh -n.");

            var home = Path.Combine(scratch, "home");
            var install = Path.Combine(home, ".tereditor", "host-install");
            var state = Path.Combine(home, ".tereditor", "host");
            var outside = Path.Combine(scratch, "outside.txt");
            Directory.CreateDirectory(Path.Combine(install, "squashfs-root"));
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "settings.toml"), "x");
            File.WriteAllText(outside, "x");
            File.WriteAllText(Path.Combine(install, "squashfs-root", "AppRun"), "#!/bin/sh\nsleep 60\n");
            File.WriteAllText(Path.Combine(install, "SHA256SUMS"), "x");
            void WriteManifest(string pidLine) => File.WriteAllText(Path.Combine(install, "manifest.txt"), string.Join('\n', HostLaunch.Marker, "state=" + state, "path=" + Path.Combine(install, "SHA256SUMS"), "path=" + Path.Combine(install, "squashfs-root"), "path=" + Path.Combine(install, "gone"), "path=" + outside, "path=" + install + "/../outside.txt", "path=" + Path.Combine(install, "host.pid")) + "\n");
            WriteManifest("");
            // A running fake host inside the folder is stopped; an unrelated process whose pid is also on file would be left alone (checked first).
            using var stranger = Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
            File.WriteAllText(Path.Combine(install, "host.pid"), stranger.Id.ToString());
            using var host = Process.Start(new ProcessStartInfo("/bin/sh", $"\"{Path.Combine(install, "squashfs-root", "AppRun")}\"") { UseShellExecute = false })!;
            try
            {
                var first = Sh(scratch, unixUninstall, home);
                Require(first.Code == 0 && !stranger.HasExited, "The uninstall must not stop a process that is not from the install folder: " + first.Output);
                Require(!Directory.Exists(install), "The uninstall must remove the install folder: " + first.Output);
                Require(File.Exists(outside) && File.Exists(Path.Combine(state, "settings.toml")), "The uninstall must keep files outside the folder and the host state by default.");
                Require(first.Output.Contains("Skipped") && first.Output.Contains("Kept " + state), "The uninstall must say what it skipped and kept: " + first.Output);
                Directory.CreateDirectory(Path.Combine(install, "squashfs-root"));
                File.WriteAllText(Path.Combine(install, "squashfs-root", "AppRun"), "#!/bin/sh\nsleep 60\n");
                File.WriteAllText(Path.Combine(install, "SHA256SUMS"), "x");
                WriteManifest("");
                File.WriteAllText(Path.Combine(install, "host.pid"), host.Id.ToString());
                var second = Sh(scratch, unixUninstall, home);
                Require(second.Code == 0 && host.WaitForExit(5000) && second.Output.Contains("Stopped the host"), "The uninstall must stop the host recorded in the install folder: " + second.Output);
                Require(Sh(scratch, unixUninstall, home).Output.Contains("Nothing to uninstall"), "A second uninstall is a no-op.");
                Require(Sh(scratch, unixUninstall.Replace("purge=0", "purge=1"), home).Code == 0, "Purge on a missing install is a no-op.");

                Directory.CreateDirectory(install);
                File.WriteAllText(Path.Combine(install, "mine.txt"), "x");
                var refused = Sh(scratch, unixUninstall, home);
                Require(refused.Code != 0 && File.Exists(Path.Combine(install, "mine.txt")), "A folder without the marker must be refused and left alone: " + refused.Output);
                File.Delete(Path.Combine(install, "mine.txt"));
                File.WriteAllText(Path.Combine(install, "manifest.txt"), HostLaunch.Marker + "\nstate=" + state + "\n");
                var purged = Sh(scratch, unixUninstall.Replace("purge=0", "purge=1"), home);
                Require(purged.Code == 0 && !Directory.Exists(state) && !Directory.Exists(install), "Purge removes the recorded state folder: " + purged.Output);
                Require(Sh(scratch, unixUninstall, "").Code != 0, "An empty HOME must stop the uninstall.");
            }
            finally
            {
                if (!stranger.HasExited) stranger.Kill();
                if (!host.HasExited) host.Kill();
            }
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static (int Code, string Output) Sh(string work, string script, string? home = null, bool parseOnly = false)
    {
        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        if (home is not null) info.Environment["HOME"] = home;
        if (parseOnly) info.ArgumentList.Add("-n");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.GetAwaiter().GetResult());
    }

    private static string FindRepo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Tereditor.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The repository root was not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}