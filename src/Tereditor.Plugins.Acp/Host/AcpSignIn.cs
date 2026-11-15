using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tereditor.Plugins.Acp.Host;

/// <summary>What one agent is known to check to sign in, in its own order. Names and file paths only: no secret is ever read.</summary>
internal sealed record AcpCredentialFacts(string Name)
{
    /// <summary>Environment variables the agent reads a token from, in the order it checks them.</summary>
    public IReadOnlyList<string> EnvNames { get; init; } = [];
    /// <summary>Variables that switch the agent to your own model provider, so no account login is needed.</summary>
    public IReadOnlyList<string> ProviderNames { get; init; } = [];
    /// <summary>Credential files, relative to the home folder, in the order the agent checks them. Only whether one exists is read.</summary>
    public IReadOnlyList<string> CredentialFiles { get; init; } = [];
    /// <summary>The command that stores a login in the OS keychain. The keychain is never queried: this only words the line.</summary>
    public string? KeychainLogin { get; init; }
    /// <summary>Whether the agent falls back to the GitHub CLI login (<c>gh</c>) after its own sources.</summary>
    public bool GitHubCli { get; init; }
    /// <summary>Whether the order of these sources is documented. Only then is the first one found marked as checked first.</summary>
    public bool KnownOrder { get; init; }
}

/// <summary>What the agent said in its last answer to <c>initialize</c> (and <c>auth/status</c>). Kept in memory for the session of the app.</summary>
internal sealed record AcpAnswer(IReadOnlyList<AcpAuthMethod> Methods, IReadOnlyList<string> Accounts);

/// <summary>
/// Works out, on the host and without reading any secret, where an agent's sign-in can come from: environment variable names in the launch
/// environment, stored credential files that exist, the GitHub CLI account, and what the agent itself offered. The per-agent table holds only
/// documented facts; an unknown agent gets just the variables set in its own entry that look like credentials.
/// </summary>
internal sealed partial class AcpSignIn(Func<string, string?> environment, string home, IReadOnlyDictionary<string, AcpCredentialFacts>? table = null)
{
    /// <summary>
    /// GitHub Copilot CLI (documented order): COPILOT_GITHUB_TOKEN, GH_TOKEN, GITHUB_TOKEN, then the OAuth token that <c>copilot login</c> keeps in the OS keychain
    /// (service <c>copilot-cli</c>), then the GitHub CLI login as the last fallback. With the COPILOT_PROVIDER_* variables no GitHub login is needed.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, AcpCredentialFacts> Known = new Dictionary<string, AcpCredentialFacts>
    {
        ["github-copilot-cli"] = new("Copilot")
        {
            EnvNames = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"],
            ProviderNames = ["COPILOT_PROVIDER_BASE_URL", "COPILOT_PROVIDER_TYPE", "COPILOT_PROVIDER_API_KEY"],
            KeychainLogin = "copilot login",
            GitHubCli = true,
            KnownOrder = true
        }
    };

    private readonly IReadOnlyDictionary<string, AcpCredentialFacts> facts = table ?? Known;
    private readonly Lock gate = new();
    private (DateTime At, string? Login, string Note)? gh;

    public static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GhCache = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"Logged in to github\.com (?:account|as) ([A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\[bot\])?)")]
    private static partial Regex LoginPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("TOKEN|KEY|SECRET|AUTH|PASSWORD|CREDENTIAL", RegexOptions.IgnoreCase)]
    private static partial Regex CredentialLike();

    private AcpCredentialFacts FactsFor(AcpAgentEntry entry) =>
        facts.GetValueOrDefault(entry.Id) ?? new AcpCredentialFacts(entry.Name) { EnvNames = [.. entry.Env.Keys.Where(name => CredentialLike().IsMatch(name))] };

    private (bool Set, string Origin) EnvOrigin(AcpAgentEntry entry, string name)
    {
        if (entry.Env.TryGetValue(name, out var own)) return (own.Length > 0, "in this agent's settings");
        return (environment(name) is { Length: > 0 }, "in your shell");
    }

    /// <param name="answer">What the agent said at its last start; null before any start.</param>
    /// <param name="startFailed">The last start failed, so the agent is known to be installed and to have run.</param>
    public async Task<AcpSignInInfo> ResolveAsync(AcpAgentEntry entry, AcpAnswer? answer, bool startFailed, CancellationToken cancellationToken)
    {
        var known = FactsFor(entry);
        // Each source in the agent's own order: its line, and whether it is there (null: it may be, and nothing looked).
        var ordered = new List<(AcpSignInLine Line, bool? Present)>();
        var why = new List<string>();
        var order = new List<string>();

        var present = new List<string>();
        var absent = new List<string>();
        foreach (var name in known.EnvNames)
        {
            order.Add(name);
            var (set, origin) = EnvOrigin(entry, name);
            if (!set) { absent.Add(name); continue; }
            present.Add(name);
            ordered.Add((new AcpSignInLine("env", name + " " + origin), true));
        }
        if (known.EnvNames.Count > 0)
        {
            if (present.Count > 0) why.Add(string.Join(", ", present) + (present.Count == 1 ? " is set" : " are set"));
            if (absent.Count > 0) why.Add(string.Join(", ", absent) + (absent.Count == 1 ? " is not set" : " are not set"));
        }

        foreach (var file in known.CredentialFiles)
        {
            order.Add("stored login file");
            var shown = "~/" + file;
            if (File.Exists(Path.Combine(home, file))) ordered.Add((new AcpSignInLine("stored", "A stored login file (" + shown + ")"), true));
            else why.Add("no stored login file at " + shown);
        }
        if (known.KeychainLogin is { } login)
        {
            order.Add("stored login");
            // The keychain may ask the user for permission when queried, so it is never queried. The line shows once the agent has run.
            if (answer is not null || startFailed) ordered.Add((new AcpSignInLine("stored", "A stored login, if you ran `" + login + "`"), null));
            else why.Add("a stored login in the OS keychain is not looked up; it shows once the agent has started");
        }

        if (known.GitHubCli)
        {
            order.Add("GitHub CLI");
            var (account, note) = await GitHubAccountAsync(cancellationToken);
            if (account is not null)
            {
                ordered.Add((new AcpSignInLine("gh", "GitHub CLI account " + account), true));
                why.Add("GitHub CLI account " + account + " is used by " + known.Name + " as the last fallback");
            }
            else why.Add(note);
        }

        var lines = new List<AcpSignInLine>();
        if (known.ProviderNames.FirstOrDefault(name => EnvOrigin(entry, name).Set) is { } provider)
            lines.Add(new AcpSignInLine("provider", "Your own model provider (" + provider + "): no account login is needed"));
        var marked = false;
        foreach (var (line, there) in ordered)
        {
            // The first source known to be there is checked first, unless an earlier one that may be there comes before it.
            var first = known.KnownOrder && !marked && there == true;
            if (there is null || first) marked = true;
            lines.Add(first ? line with { CheckedFirst = true } : line);
        }

        var methods = new List<AcpSignInMethod>();
        foreach (var method in answer?.Methods ?? [])
        {
            var commandLine = CommandLine(entry, method);
            methods.Add(new AcpSignInMethod(method.Id, method.Name, method.Description) { Label = method.Label, CommandLine = commandLine });
            lines.Add(new AcpSignInLine("agent", "A sign-in the agent will ask for: " + method.Name + (method.Label is { Length: > 0 } label && label != method.Name ? " (" + label + ")" : "")));
        }
        foreach (var account in answer?.Accounts ?? []) lines.Add(new AcpSignInLine("agent", "The agent says it uses: " + account));
        if (lines.Count == 0) lines.Add(new AcpSignInLine("none", "Nothing found. The agent asks when it needs you to sign in."));

        if (order.Count > 0) why.Add(known.Name + " checks them in this order: " + string.Join(", ", order) + ". It has not told us which one it used");
        else why.Add("This agent's sign-in sources are not known; only the credential-like variables in its settings and what it says itself are shown");
        return new AcpSignInInfo(entry.Id, lines, string.Join("; ", why) + ".", methods);
    }

    /// <summary>
    /// The logged-in account of the GitHub CLI for github.com, from <c>gh auth status --hostname github.com</c> (never <c>gh auth token</c>; the token is never printed or read).
    /// Token variables are removed for the child, so it reports the stored login and not the variable. Only the account name is kept from the output.
    /// </summary>
    private async Task<(string? Login, string Note)> GitHubAccountAsync(CancellationToken cancellationToken)
    {
        lock (gate)
            if (gh is { } cached && DateTime.UtcNow - cached.At < GhCache) return (cached.Login, cached.Note);
        var result = await Task.Run(() => RunGhAsync(cancellationToken), cancellationToken);
        lock (gate) gh = (DateTime.UtcNow, result.Login, result.Note);
        return result;
    }

    private async Task<(string? Login, string Note)> RunGhAsync(CancellationToken cancellationToken)
    {
        var program = FindOnPath("gh");
        if (program is null) return (null, "the GitHub CLI (gh) was not found");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GhTimeout);
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in (string[])["auth", "status", "--hostname", "github.com"]) start.ArgumentList.Add(argument);
            foreach (var name in (string[])["GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN"]) start.Environment.Remove(name);
            start.Environment["GH_PROMPT_DISABLED"] = "1";
            start.Environment["NO_COLOR"] = "1";
            start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
            if (environment("PATH") is { } path) start.Environment["PATH"] = path;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("gh could not be started.");
            process.StandardInput.Close();
            // Older versions print the status on the error stream, newer ones on the output: read both, keep only the account name.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var text = await output + "\n" + await error;
                return LoginPattern().Match(text) is { Success: true } match ? (match.Groups[1].Value, "") : (null, "no GitHub CLI login was found for github.com");
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (null, "the GitHub CLI (gh) did not answer in time"); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return (null, "the GitHub CLI (gh) could not be run"); }
    }

    private string? FindOnPath(string name)
    {
        string[] names = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd", name + ".bat"] : [name];
        foreach (var directory in (environment("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var candidate in names)
                if (File.Exists(Path.Combine(directory, candidate))) return Path.Combine(directory, candidate);
        return null;
    }

    /// <summary>
    /// The exact line a terminal runs for a terminal login: the method's own program (a bare name that is the entry's program is the entry's launch command)
    /// or, for <c>type: "terminal"</c>, the entry's launch command and arguments with the method's arguments added. The method's own variables come first.
    /// The entry's environment is never put in the line, so a token set there stays out of it. Null for a method that is not a terminal login.
    /// </summary>
    internal static string? CommandLine(AcpAgentEntry entry, AcpAuthMethod method)
    {
        if (!method.Terminal) return null;
        IReadOnlyList<string> parts = method.Command is { Length: > 0 } command
            ? [BareIsEntry(entry, command) ? entry.LaunchCommand() : command, .. method.Args]
            : [entry.LaunchCommand(), .. entry.LaunchArgs(), .. method.Args];
        IEnumerable<string> variables = OperatingSystem.IsWindows() ? [] : method.Env.Where(pair => NamePattern().IsMatch(pair.Key)).Select(pair => pair.Key + "=" + ShellQuote(pair.Value));
        return string.Join(' ', variables.Concat(parts.Select(ShellQuote)));
    }

    private static bool BareIsEntry(AcpAgentEntry entry, string command) =>
        command.IndexOfAny(['/', '\\']) < 0 && command == Path.GetFileNameWithoutExtension(entry.LaunchCommand());

    internal static string ShellQuote(string text) =>
        text.Length > 0 && text.All(c => char.IsAsciiLetterOrDigit(c) || "_@%+=:,./-".Contains(c)) ? text
        : OperatingSystem.IsWindows() ? "\"" + text.Replace("\"", "\\\"") + "\""
        : "'" + text.Replace("'", "'\\''") + "'";
}