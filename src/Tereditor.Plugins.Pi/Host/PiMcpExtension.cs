using Tereditor.Plugins.Agent.Host;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>
/// The tiny Pi extension that gives a chat's Pi tereditor's MCP server for that run only. Pi loads it with <c>-e</c> and it registers
/// the server named by <c>THINKRAIL_MCP_URL</c> (the host's address keyed to the chat), so neither <c>~/.pi</c> nor the project is written.
/// </summary>
internal static class PiMcpExtension
{
    public const string Variable = "THINKRAIL_MCP_URL";
    public const string ServerName = "tereditor";

    private const string Source = """
        // Written by tereditor. It registers tereditor's MCP server for this run; nothing is saved to mcp.json.
        export default function (pi) {
          const url = process.env.THINKRAIL_MCP_URL;
          if (!url) return;
          pi.registerMcpServer("tereditor", { url, exposure: "codemode", description: "tereditor's project tools: language servers, code search, workspaces, processes and resources." });
        }

        """;

    public static string DefaultDirectory() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "pi");

    /// <summary>The extension file in <paramref name="directory"/>, written when it is missing or different (atomically, since chats share it).</summary>
    public static string Ensure(string directory)
    {
        var path = Path.Combine(directory, "tereditor-mcp.mjs");
        OwnedFile.Write(path, Source.Replace("\r\n", "\n"));
        return path;
    }
}