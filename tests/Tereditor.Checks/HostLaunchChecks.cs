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
            Require(firstPanel.Children.Single().Name == "Launch_macos-script" && otherPanel.Children.Select(card => card.Name).SequenceEqual(["Launch_windows-script", "Launch_linux-script"]), "macOS: its option first, the others under Other options.");
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
        }
        finally { window.Close(); }
        Console.WriteLine("PASS host launch: system detection, option order, release asset names, Copy buttons and the local-access notice");
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