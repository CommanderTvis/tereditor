using System.Net;
using System.Security.Cryptography;

namespace Tereditor.Host.Remote;

/// <summary>The standalone host's startup, shared by <c>tereditor-host</c> and the desktop app's <c>--host-only</c> (no window, no Avalonia).</summary>
public static class HostMain
{
    public static int Run()
    {
        RunAsync().GetAwaiter().GetResult();
        return 0;
    }

    private static async Task RunAsync()
    {
        var root = Environment.GetEnvironmentVariable("TEREDITOR_ROOT") ?? Directory.GetCurrentDirectory();
        var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("TEREDITOR_BIND") ?? "127.0.0.1");
        var port = int.Parse(Environment.GetEnvironmentVariable("TEREDITOR_PORT") ?? "54123");
        // Without a token the host makes a random one nobody sees: browsers pair with the code printed below, other clients cannot connect.
        var configured = Environment.GetEnvironmentVariable("TEREDITOR_TOKEN");
        var token = string.IsNullOrWhiteSpace(configured) ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) : configured;
        // Shared settings, projects and labels persist on the host machine, apart from any local app state.
        var state = Environment.GetEnvironmentVariable("TEREDITOR_STATE_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "host");
        // Pairing is for browsers on this machine, so it exists only on a loopback bind. TEREDITOR_BROWSER_ORIGINS adds exact origins to the published one.
        var loopback = IPAddress.IsLoopback(bind);
        var pairing = loopback ? new PairingCodes() : null;
        var origins = (Environment.GetEnvironmentVariable("TEREDITOR_BROWSER_ORIGINS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        using var browsers = loopback ? new LocalBrowserConnections((_, _) => Task.FromResult(false), pairing, origins) : null;
        await using var app = RemoteServer.Create(root, bind, port, token, state, browserRoot: Environment.GetEnvironmentVariable("TEREDITOR_BROWSER_ROOT"), headlessLanguageMcp: Environment.GetEnvironmentVariable("TEREDITOR_HEADLESS_MCP") == "1", browsers: browsers);
        await app.StartAsync();
        Console.WriteLine($"TEREDITOR_HOST_READY {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        if (pairing is not null)
        {
            // The code goes to this terminal only: never a log file, a URL or the state directory.
            void Show(string code) => Console.WriteLine($"Host address: localhost:{port}\nPairing code: {code} (valid for 5 minutes, works once)\nIn the browser, enter the address and the code. Press Enter here for a new code.");
            pairing.Reissued += Show;
            Show(pairing.Issue());
            if (!Console.IsInputRedirected) _ = Task.Run(() => { while (Console.ReadLine() is not null) Show(pairing.Issue()); });
        }
        await app.WaitForShutdownAsync();
    }
}