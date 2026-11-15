namespace Tereditor.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// Moves a skill folder between the user's skills directory (<c>~/.claude/skills</c>) and the project's
/// (<c>&lt;project&gt;/.claude/skills</c>). Nothing is overwritten, plugin and managed skills are not folders here so they
/// are never moved, and a failed move leaves the skill where it was.
/// </summary>
internal static class ClaudeSkillMove
{
    /// <summary>Checks reach the copy path and its failure handling through these; the pane never sets them.</summary>
    internal sealed record Seams
    {
        /// <summary>Skip the rename, as when the two folders are on different disks.</summary>
        public bool ForceCopy { get; init; }

        /// <summary>Called with each file's destination before it is copied; throwing here fails the copy part-way.</summary>
        public Action<string>? BeforeCopy { get; init; }
    }

    private static string SkillsDirectory(string root, ClaudeWritableScope scope) =>
        ClaudePaths.SkillDirectories(root).First(entry => entry.Scope.ToString() == scope.ToString()).Path;

    private static bool Same(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    // A dangling link is neither a file nor a directory, but it still occupies the name.
    private static bool Occupied(string path) => Directory.Exists(path) || File.Exists(path) || new FileInfo(path).LinkTarget is not null;

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The paths a move would use and the plain reason it cannot happen; reads the disk, writes nothing.</summary>
    public static SkillMoveInfo Plan(string root, string name, ClaudeWritableScope from, ClaudeWritableScope to)
    {
        if (from == to || from == ClaudeWritableScope.Local || to == ClaudeWritableScope.Local)
            return new("", "", "A skill moves between your user folder and the project's folder.");
        var fromDirectory = SkillsDirectory(root, from);
        var toDirectory = SkillsDirectory(root, to);
        var source = Path.Combine(fromDirectory, name);
        var target = Path.Combine(toDirectory, name);
        SkillMoveInfo Refuse(string problem) => new(source, target, problem);
        // The name comes from the client: a single folder name, never a path.
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name != name.Trim())
            return Refuse("That is not a skill name.");
        if (!Directory.Exists(root)) return Refuse("No project is open.");
        if (Same(fromDirectory, toDirectory)) return Refuse("The project's skills folder is your user skills folder, so there is nowhere to move it.");
        var info = new DirectoryInfo(source);
        var link = info.LinkTarget;
        if (!info.Exists && link is null)
            return Refuse($"There is no skill folder \"{name}\" in {fromDirectory}. Skills from plugins or managed settings cannot be moved.");
        if (Occupied(target)) return Refuse($"\"{name}\" already exists in {toDirectory}. Nothing is overwritten.");
        if (link is not null)
        {
            // The link itself moves with its target text unchanged. An absolute one keeps reaching the same place; a relative one
            // is read from the new folder, so it would reach somewhere else (the two folders never share a parent).
            if (!Path.IsPathRooted(link))
                return Refuse($"\"{name}\" is a link to \"{link}\", a path relative to its folder, and would point somewhere else after the move. Make the link absolute first.");
            return new(source, target, null);
        }
        if (BrokenLink(source) is { } broken)
            return Refuse($"\"{name}\" has a relative link, \"{broken}\", that leaves the skill folder and would break after the move.");
        return new(source, target, null);
    }

    // A relative link inside the folder that reaches outside it, as the path it is written with.
    private static string? BrokenLink(string folder)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly))
        {
            if (entry.LinkTarget is { } target)
            {
                if (!Path.IsPathRooted(target) && !IsInside(Path.GetFullPath(target, Path.GetDirectoryName(entry.FullName)!), folder) &&
                    !Same(Path.GetFullPath(target, Path.GetDirectoryName(entry.FullName)!), folder)) return target;
            }
            else if (entry is DirectoryInfo directory && BrokenLink(directory.FullName) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>Moves the skill, or throws with the reason; either way no half-moved folder is left behind.</summary>
    public static SkillMoveInfo Run(string root, string name, ClaudeWritableScope from, ClaudeWritableScope to, Seams? seams = null)
    {
        var plan = Plan(root, name, from, to);
        if (plan.Problem is { } problem) throw new InvalidOperationException(problem);
        var toDirectory = Path.GetDirectoryName(plan.To)!;
        var createdDirectory = !Directory.Exists(toDirectory);
        try
        {
            Directory.CreateDirectory(toDirectory);
            var info = new DirectoryInfo(plan.From);
            if (info.LinkTarget is { } link) MoveLink(plan, link, info.Exists);
            else MoveFolder(plan, seams);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (createdDirectory) TryDelete(toDirectory, recursive: false);
            throw new InvalidOperationException($"Could not move \"{name}\": {error.Message} Nothing was changed.");
        }
        catch
        {
            if (createdDirectory) TryDelete(toDirectory, recursive: false);
            throw;
        }
        return plan;
    }

    private static void MoveLink(SkillMoveInfo plan, string link, bool toFolder)
    {
        if (toFolder) Directory.CreateSymbolicLink(plan.To, link); else File.CreateSymbolicLink(plan.To, link);
        try
        {
            // A folder link is removed as a link on Windows and as a file on the other systems; either leaves its target alone.
            if (toFolder && OperatingSystem.IsWindows()) Directory.Delete(plan.From); else File.Delete(plan.From);
        }
        catch
        {
            TryDeleteLink(plan.To, toFolder);
            throw;
        }
    }

    private static void MoveFolder(SkillMoveInfo plan, Seams? seams)
    {
        if (seams?.ForceCopy != true)
        {
            try
            {
                Directory.Move(plan.From, plan.To);
                return;
            }
            // Another disk cannot be renamed into; anything else that went wrong is told as it is.
            catch (IOException) when (Directory.Exists(plan.From) && !Occupied(plan.To)) { }
        }
        // Copy beside the target under a temporary name, rename it into place, and delete the source last.
        var temporary = Path.Combine(Path.GetDirectoryName(plan.To)!, $".moving-{Path.GetFileName(plan.To)}-{Guid.NewGuid():N}");
        try
        {
            Copy(plan.From, temporary, seams);
            Directory.Move(temporary, plan.To);
        }
        catch
        {
            MakeWritable(temporary);
            TryDelete(temporary, recursive: true);
            throw;
        }
        try { Directory.Delete(plan.From, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Copied the skill to {plan.To}, but could not remove the original in {plan.From}: {error.Message} It now exists in both places.");
        }
    }

    // Files keep their permission bits and times; links are recreated, never followed.
    private static void Copy(string source, string destination, Seams? seams)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var copy = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } target)
            {
                if (entry is DirectoryInfo) Directory.CreateSymbolicLink(copy, target); else File.CreateSymbolicLink(copy, target);
            }
            else if (entry is DirectoryInfo directory) Copy(directory.FullName, copy, seams);
            else
            {
                seams?.BeforeCopy?.Invoke(copy);
                File.Copy(entry.FullName, copy);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(copy, File.GetUnixFileMode(entry.FullName));
                File.SetLastWriteTimeUtc(copy, entry.LastWriteTimeUtc);
            }
        }
        // The folder's own mode last, so a read-only folder can still be filled.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        Directory.SetLastWriteTimeUtc(destination, Directory.GetLastWriteTimeUtc(source));
    }

    private static void MakeWritable(string folder)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(folder)) return;
        try
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
                if (new DirectoryInfo(directory).LinkTarget is null) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string folder, bool recursive)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteLink(string path, bool toFolder)
    {
        try { if (toFolder && OperatingSystem.IsWindows()) Directory.Delete(path); else File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
