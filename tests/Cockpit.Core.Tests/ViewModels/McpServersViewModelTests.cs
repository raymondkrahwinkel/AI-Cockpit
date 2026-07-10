using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The MCP-servers dialog logic (#26): loading the registry into editable rows, add/remove, and
/// persisting the edited list — with per-transport validation and the http/stdio field split in
/// <see cref="EditableMcpServerViewModel.ToConfig"/>.
/// </summary>
public class McpServersViewModelTests
{
    [Fact]
    public void AddServer_AppendsANewRowAndSelectsIt()
    {
        var vm = new McpServersViewModel(Substitute.For<IMcpServerStore>());

        vm.AddServerCommand.Execute(null);

        vm.Servers.Should().ContainSingle();
        vm.SelectedServer.Should().Be(vm.Servers[0]);
    }

    [Fact]
    public void AddPreset_AddsThePrefilledTemplateAndSelectsIt()
    {
        var vm = new McpServersViewModel(Substitute.For<IMcpServerStore>());
        var filesystem = vm.Presets.First(preset => preset.Label == "Filesystem");

        vm.AddPresetCommand.Execute(filesystem);

        var added = vm.SelectedServer!;
        added.Name.Should().Be("filesystem");
        added.Command.Should().Be("npx");
        added.Args.Should().Contain("@modelcontextprotocol/server-filesystem");
    }

    [Fact]
    public void AddPreset_Twice_GivesTheSecondAUniqueName()
    {
        var vm = new McpServersViewModel(Substitute.For<IMcpServerStore>());
        var filesystem = vm.Presets.First(preset => preset.Label == "Filesystem");

        vm.AddPresetCommand.Execute(filesystem);
        vm.AddPresetCommand.Execute(filesystem);

        vm.Servers.Select(server => server.Name).Should().Equal("filesystem", "filesystem-2");
    }

    [Fact]
    public async Task LoadAsync_PopulatesRowsFromTheStore()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new McpServerConfig { Name = "github", Transport = McpTransport.Http, Url = "https://x/mcp" } });
        var vm = new McpServersViewModel(store);

        await vm.LoadAsync();

        vm.Servers.Should().ContainSingle().Which.Name.Should().Be("github");
    }

    [Fact]
    public async Task Save_PersistsTheServers_AndCloses()
    {
        var store = Substitute.For<IMcpServerStore>();
        var vm = new McpServersViewModel(store);
        vm.AddServerCommand.Execute(null);
        var row = vm.SelectedServer!;
        row.Name = "fs";
        row.Command = "npx";
        row.Args = "-y\n@modelcontextprotocol/server-filesystem\n.";
        var closed = false;
        vm.CloseRequested += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list =>
                list.Count == 1 && list[0].Name == "fs" && list[0].Command == "npx" && list[0].Args.Count == 3),
            Arg.Any<CancellationToken>());
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task Save_WithAStdioServerMissingItsCommand_DoesNotPersist()
    {
        var store = Substitute.For<IMcpServerStore>();
        var vm = new McpServersViewModel(store);
        vm.AddServerCommand.Execute(null);
        var row = vm.SelectedServer!;
        row.Name = "fs";
        row.Command = "";

        await vm.SaveCommand.ExecuteAsync(null);

        await store.DidNotReceive().SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>());
        vm.StatusMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ToConfig_ForHttpApiKey_KeepsUrlAndKey_AndDropsStdioFields()
    {
        var editable = new EditableMcpServerViewModel(new McpServerConfig
        {
            Name = "x",
            Transport = McpTransport.Http,
            Url = "https://x/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "k",
        })
        {
            Command = "npx",     // stale stdio values that must be dropped for http
            Args = "-y\nfoo",
        };

        var config = editable.ToConfig();

        config.Command.Should().BeNull();
        config.Args.Should().BeEmpty();
        config.Url.Should().Be("https://x/mcp");
        config.Auth.Should().Be(McpServerAuth.ApiKey);
        config.ApiKey.Should().Be("k");
    }
}
