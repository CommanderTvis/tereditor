// Shared by the native build recipes (the Ghostty, WASM, Tailscale and Headscale scripts in scripts/) through `#:include`, with
// lib/Tasks.cs. It is the machine-wide cache every checkout shares ($TEREDITOR_CACHE_DIR, which mise.toml defines; the fallback here
// is the same path). A result is a directory named by a key that hashes everything its build reads, so checkouts on different
// branches each keep their own and a new worktree reuses one. Results are built in `<result>.partial` and published by rename,
// and a checkout's copy replaces each output by rename, so neither a concurrent reader nor an interrupted build ever sees a
// half-written library. Recipes hold Tasks.AcquireLock on the cache entry while they write.
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using static Tasks;

static class Native
{
    /// <summary>The recipe being run: the entry-point script, which each recipe's fingerprint hashes.</summary>
    public static string Recipe => AppContext.GetData("EntryPointFilePath") as string ?? throw new TaskFailure("Run the recipe with dotnet run --file.");

    /// <summary>A pinned value of mise.toml's [env].</summary>
    public static string Pin(string name) => Env(name) ?? throw new TaskFailure("Run through scripts/mise x -- (see mise.toml).");

    /// <summary>The cache root, created on first use; on macOS a new root is excluded from Time Machine.</summary>
    public static string CacheDir()
    {
        var root = Env("TEREDITOR_CACHE_DIR") ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "tereditor", "cache")
            : Path.Combine(Env("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".cache"), "tereditor"));
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            if (OperatingSystem.IsMacOS())
                try { Capture("tmutil", ["addexclusion", root], discardError: true); } catch (Exception) { }
        }
        return root;
    }

    /// <summary>Zig's global package cache: ZIG_GLOBAL_CACHE_DIR, which mise.toml defines, or Zig's own default.</summary>
    public static string ZigCacheDir() => Env("ZIG_GLOBAL_CACHE_DIR") ?? (OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "zig")
        : Path.Combine(Env("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".cache"), "zig"));

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>A short hash of a fingerprint, used as a result directory name.</summary>
    public static string CacheKey(string text) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>The SHA-256 of the files' concatenated bytes, as lowercase hex.</summary>
    public static string HashFiles(params string[] paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths) hash.AppendData(File.ReadAllBytes(path));
        return Hex(hash.GetHashAndReset());
    }

    /// <summary>What `cat files | shasum -a 256` prints (the hex, two spaces and a dash): the fingerprints embed it in this form.</summary>
    public static string ShaSum(params string[] paths) => HashFiles(paths) + "  -";

    public static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Hex(SHA256.HashData(stream));
    }

    /// <summary>Removes a file or tree, also its read-only files (Git's object files, which Windows will not delete otherwise).</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null && OperatingSystem.IsWindows())
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        DeleteAll(path);
    }

    /// <summary>Moves the finished &lt;result&gt;.partial to &lt;result&gt;.</summary>
    public static void PublishResult(string result)
    {
        Delete(result);
        Directory.Move(result + ".partial", result);
    }

    /// <summary>Copies each named file or directory of a result into the output directory, replacing the previous one by rename.</summary>
    public static void InstallOutput(string result, string output, params string[] names)
    {
        Directory.CreateDirectory(output);
        foreach (var name in names)
        {
            var partial = Path.Combine(output, name + ".partial");
            var target = Path.Combine(output, name);
            Delete(partial);
            Copy(Path.Combine(result, name), partial);
            if (Directory.Exists(partial))
            {
                Delete(target);
                Directory.Move(partial, target);
            }
            else File.Move(partial, target, true);
        }
    }

    /// <summary>Updates a file's modification time, as touch does.</summary>
    public static void Touch(params string[] paths)
    {
        foreach (var path in paths) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    /// <summary>The contents of a stamp file without its trailing newline, or null when it is missing.</summary>
    public static string? ReadStamp(string path) => File.Exists(path) ? File.ReadAllText(path).TrimEnd('\n', '\r') : null;

    /// <summary>The commit a Git checkout is at, or "" when the directory is no checkout.</summary>
    public static string GitHead(string directory)
    {
        if (!Directory.Exists(directory)) return "";
        var (code, output) = Capture("git", ["-C", directory, "rev-parse", "HEAD"], discardError: true);
        return code == 0 ? output.Trim() : "";
    }

    /// <summary>Checks out one pinned commit of a Git repository into a new directory. A commit id is its own checksum, so this is the fetcher for sources pinned by revision; archives are pinned by checksum in mise.toml and installed by mise instead.</summary>
    public static void FetchGit(string directory, string url, string commit)
    {
        Check("git", ["init", "-q", directory]);
        Check("git", ["-C", directory, "fetch", "-q", "--depth", "1", url, commit]);
        Check("git", ["-C", directory, "checkout", "-q", "FETCH_HEAD"]);
        if (GitHead(directory) != commit) throw new TaskFailure($"Unexpected revision of {url}");
    }

    public const string GhosttyUrl = "https://github.com/ghostty-org/ghostty.git";

    /// <summary>Makes &lt;work&gt;/&lt;name&gt; a checkout of the pinned Ghostty commit, fetching it when it is at another one.</summary>
    public static string GhosttySource(string work, string name, string commit)
    {
        var directory = Path.Combine(work, name);
        if (GitHead(directory) != commit)
        {
            Delete(directory);
            Delete(directory + ".partial");
            FetchGit(directory + ".partial", GhosttyUrl, commit);
            Directory.Move(directory + ".partial", directory);
        }
        return directory;
    }

    /// <summary>Writes `git show &lt;commit&gt;:&lt;path&gt;` of a checkout to a file, byte for byte.</summary>
    public static void GitShowToFile(string directory, string commit, string path, string destination)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-C", directory, "show", $"{commit}:{path}" }) info.ArgumentList.Add(argument);
        using var child = Process.Start(info) ?? throw new TaskFailure("Cannot start git.");
        using (var output = File.Create(destination)) child.StandardOutput.BaseStream.CopyTo(output);
        child.WaitForExit();
        if (child.ExitCode != 0) throw new TaskFailure($"git show {commit}:{path} failed with exit code {child.ExitCode}", child.ExitCode);
    }

    /// <summary>Applies a patch to a source tree (patch -s -p1).</summary>
    public static void ApplyPatch(string directory, string patch) => Check("patch", ["-s", "-d", directory, "-p1", "-i", patch]);

    /// <summary>Appends the bytes of the files to a target file (cat files &gt;&gt; target).</summary>
    public static void AppendFiles(string target, params string[] files)
    {
        using var output = new FileStream(target, FileMode.Append);
        foreach (var file in files)
        {
            using var input = File.OpenRead(file);
            input.CopyTo(output);
        }
    }

    /// <summary>The Zig executable of a pin of mise.toml (`scripts/tool` prints, and installs when missing, its directory).</summary>
    public static string ZigExe(string version)
    {
        var directory = Tool("zig-" + version);
        var zig = Path.Combine(directory, OperatingSystem.IsWindows() ? "zig.exe" : "zig");
        return File.Exists(zig) ? zig : throw new TaskFailure($"No Zig {version} in {directory}.");
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Downloads a URL to a file, retrying a few times; a failed download leaves no file.</summary>
    public static void Download(string url, string path, int attempts)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using (var stream = response.Content.ReadAsStream())
                using (var file = File.Create(path)) stream.CopyTo(file);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
            {
                DeleteAll(path);
                if (attempt >= attempts) throw new TaskFailure($"Cannot download {url}: {exception.Message}");
                Thread.Sleep(2000 * attempt);
            }
        }
    }

    /// <summary>Fails unless a file has the SHA-256 the pin names.</summary>
    public static void VerifySha256(string path, string checksum)
    {
        if (!string.Equals(FileHash(path), checksum, StringComparison.OrdinalIgnoreCase))
            throw new TaskFailure($"{Path.GetFileName(path)} does not match its pinned SHA-256 {checksum}.");
    }

    /// <summary>
    /// Restores the Zig packages hosted on Codeberg (neither GitHub nor Ghostty's deps.files.ghostty.org CDN, which are fetched upstream by design)
    /// into Zig's global cache before a native build, so CI does not depend on codeberg.org at build time. scripts/zig-deps.lock pins a
    /// mirror archive (a release asset of this repository, built by scripts/mise run zig-deps:mirror) by SHA-256 and lists each
    /// package with its upstream URL. Zig fetches nothing for a package whose p/ entry exists, whatever its URL says. Only absent
    /// packages are restored, so a warm cache needs no network. A missing or corrupt mirror falls back to the upstream URLs Zig
    /// uses; if one of those is unreachable too, this fails naming the package and URL.
    /// </summary>
    public static void RestoreZigDeps()
    {
        if (Env("ZIG_DEPS_NO_RESTORE") is not null) return;
        var lines = File.ReadAllLines(At("scripts", "zig-deps.lock"))
            .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).Where(fields => fields.Length > 0).ToList();
        var cache = ZigCacheDir();
        var packages = Path.Combine(cache, "p");
        Directory.CreateDirectory(packages);
        var missing = lines.Where(fields => fields[0] == "pkg" && fields.Length > 2)
            .Select(fields => (Package: fields[1], Url: fields[2]))
            .Where(package => !File.Exists(Path.Combine(packages, package.Package + ".tar.gz")) && !Directory.Exists(Path.Combine(packages, package.Package + ".tar.gz")))
            .ToList();
        if (missing.Count == 0) return;

        string Field(string key) => lines.FirstOrDefault(fields => fields[0] == key && fields.Length > 1)?[1] ?? "";
        var (repo, tag, asset, sha) = (Field("repo"), Field("tag"), Field("asset"), Field("sha256"));
        var archive = Path.Combine(cache, asset);
        var fetched = false;
        if (File.Exists(archive) && FileHash(archive) == sha) fetched = true;
        else
        {
            try
            {
                Download($"https://github.com/{repo}/releases/download/{tag}/{asset}", archive + ".partial", 4);
                if (FileHash(archive + ".partial") == sha)
                {
                    File.Move(archive + ".partial", archive, true);
                    fetched = true;
                }
            }
            catch (TaskFailure) { }
            DeleteAll(archive + ".partial");
        }

        if (fetched)
        {
            var temporary = Path.Combine(cache, $"zig-deps-extract.{Environment.ProcessId}");
            Directory.CreateDirectory(temporary);
            try
            {
                Check("tar", ["-xzf", archive, "-C", temporary]);
                foreach (var (package, _) in missing)
                {
                    var entry = package + ".tar.gz";
                    if (File.Exists(Path.Combine(packages, entry)) || !File.Exists(Path.Combine(temporary, "p", entry))) continue;
                    try { File.Move(Path.Combine(temporary, "p", entry), Path.Combine(packages, entry)); } catch (IOException) { }
                }
            }
            finally { Delete(temporary); }
            Console.WriteLine($"Restored Zig packages from {asset} into {packages}.");
            return;
        }

        Console.WriteLine($"::warning title=Zig dependency mirror unavailable::{asset} (release {tag} of {repo}) could not be downloaded or failed its SHA-256 check; Zig will fetch the packages from their upstream URLs.");
        var failed = false;
        foreach (var (package, url) in missing)
        {
            var probe = url.StartsWith("git+", StringComparison.Ordinal) ? url[4..].Split('#')[0] + "/info/refs?service=git-upload-pack" : url;
            if (Reachable(probe)) continue;
            Console.Error.WriteLine($"::error title=Zig dependency unreachable::Package {package} is not mirrored and its upstream {url} is unreachable. Publish the mirror (scripts/mise run zig-deps:mirror) or retry when the host is back.");
            failed = true;
        }
        if (failed) throw new TaskFailure("");
    }

    static bool Reachable(string url)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException) { }
            if (attempt < 3) Thread.Sleep(1000);
        }
        return false;
    }
}
