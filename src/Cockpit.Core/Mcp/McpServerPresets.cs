namespace Cockpit.Core.Mcp;

// A one-click template for a well-known MCP server (#26): pick a preset, adjust it (e.g. filesystem folder),
// and save into the shared registry. Every call stays gated by the approval prompt, and filesystem defaults
// to one folder, not the whole disk — access stays consent-scoped by design.
public sealed record McpServerPreset(string Label, string Description, McpServerConfig Template);

// The built-in preset catalogue offered in the MCP-servers dialog's quick-add row.
public static class McpServerPresets
{
    // The npm package behind the built-in filesystem preset. The delegated-tool gate keys its AC-100 fallback
    // classification on this package name — never on the bare server/tool name — so the "folder-scoped write ⇒
    // Write not Destructive" guarantee applies only to this first-party server, not any lookalike tool name.
    public const string FilesystemServerPackage = "@modelcontextprotocol/server-filesystem";

    // The presets, in the order shown. Filesystem defaults to the user's profile folder — a starting point to
    // narrow to the target project. Each description calls out its runtime prerequisite (Node/npx or
    // Python/uvx) so a preset that can't launch is a clear, not a silent, miss.
    public static IReadOnlyList<McpServerPreset> All { get; } =
    [
        // Filesystem/Fetch/Git duplicate tools Claude Code already has (Read/Write, WebFetch, Bash git), so
        // they default to LocalOnly — the local models that lack them. Memory has no Claude equivalent → All.
        new(
            "Filesystem",
            "Read and write files under one folder (needs Node/npx). Defaults to your user folder — narrow the last argument to the project you want the model to reach.",
            new McpServerConfig
            {
                Name = "filesystem",
                Transport = McpTransport.Stdio,
                Scope = McpServerScope.LocalOnly,
                Command = "npx",
                Args = ["-y", FilesystemServerPackage, DefaultFilesystemRoot()],
            }),
        new(
            "Fetch",
            "Fetch a web page and return it as text/markdown (needs Python/uvx).",
            new McpServerConfig
            {
                Name = "fetch",
                Transport = McpTransport.Stdio,
                Scope = McpServerScope.LocalOnly,
                Command = "uvx",
                Args = ["mcp-server-fetch"],
            }),
        new(
            "Git",
            "Inspect and query a local git repository (needs Python/uvx). Set the repository path in the last argument.",
            new McpServerConfig
            {
                Name = "git",
                Transport = McpTransport.Stdio,
                Scope = McpServerScope.LocalOnly,
                Command = "uvx",
                Args = ["mcp-server-git", "--repository", DefaultFilesystemRoot()],
            }),
        new(
            "Memory",
            "A simple persistent knowledge-graph the model can store and recall notes in (needs Node/npx).",
            new McpServerConfig
            {
                Name = "memory",
                Transport = McpTransport.Stdio,
                Command = "npx",
                Args = ["-y", "@modelcontextprotocol/server-memory"],
            }),
    ];

    // The built-in servers every local-model session gets automatically (#26). Local models have no tools of
    // their own, so these ship on by default; a registry entry with the same name overrides the built-in
    // (e.g. to point filesystem at a different folder), and disabling it there removes it.
    public static IReadOnlyList<McpServerConfig> LocalDefaults { get; } = [.. All.Select(preset => preset.Template)];

    private static string DefaultFilesystemRoot() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
