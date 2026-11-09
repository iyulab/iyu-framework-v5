using Iyu.VaultAi.FolderChatbots;
using Xunit;

namespace Iyu.Tests.VaultAi;

public class FolderChatbotMcpConfigTests
{
    private static McpParseResult Parse(string json) => FolderChatbotMcpConfig.Parse(json);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("{}")]
    [InlineData("""{"other": 1}""")]
    public void No_file_no_text_or_no_mcpServers_is_an_empty_result_which_cleans_the_bots_connectors_up(string? json)
    {
        var result = FolderChatbotMcpConfig.Parse(json);

        Assert.Empty(result.Entries);
        Assert.Empty(result.Issues);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void A_url_entry_and_a_connector_entry_both_parse()
    {
        var result = Parse("""
            {
              // comments and trailing commas are tolerated
              "mcpServers": {
                "crm": { "url": "https://mcp.example/crm", "headers": { "Authorization": "Bearer {{CRM_KEY}}" }, },
                "shared": { "connector": "Company wiki" }
              }
            }
            """);

        Assert.False(result.HasErrors);
        var crm = Assert.Single(result.Entries, e => e.Name == "crm");
        Assert.Equal("https://mcp.example/crm", crm.Url);
        Assert.Null(crm.ConnectorRef);
        Assert.Equal("Bearer {{CRM_KEY}}", crm.Headers["Authorization"]);
        var shared = Assert.Single(result.Entries, e => e.Name == "shared");
        Assert.Null(shared.Url);
        Assert.Equal("Company wiki", shared.ConnectorRef);
    }

    [Theory]
    [InlineData("""{"mcpServers": {"x": {"command": "node", "args": ["server.js"]}}}""", "not supported")]
    [InlineData("""{"mcpServers": {"x": {"url": "https://a", "connector": "b"}}}""", "cannot be used together")]
    [InlineData("""{"mcpServers": {"x": {}}}""", "url or connector is required")]
    [InlineData("""{"mcpServers": {"x": {"url": "ftp://a/b"}}}""", "http:// or https://")]
    [InlineData("""{"mcpServers": {"x": {"url": "relative/path"}}}""", "absolute address")]
    [InlineData("""{"mcpServers": {"x": {"connector": ""}}}""", "non-empty string")]
    [InlineData("""{"mcpServers": {"x": {"connector": "c", "headers": {}}}}""", "cannot be used on a connector entry")]
    [InlineData("""{"mcpServers": {"x": {"url": "https://a", "headers": []}}}""", "Must be an object")]
    [InlineData("""{"mcpServers": {"x": {"url": "https://a", "headers": {"h": 5}}}}""", "Must be a string")]
    [InlineData("""{"mcpServers": {"bad name": {"url": "https://a"}}}""", "1 to 64 letters")]
    [InlineData("""{"mcpServers": {"x": 5}}""", "must be an object")]
    public void An_entry_with_an_error_is_left_out_and_reported(string json, string expected)
    {
        var result = Parse(json);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Issues, i => !i.IsWarning && i.Message.Contains(expected));
    }

    [Fact]
    public void One_bad_entry_does_not_take_the_good_ones_with_it_but_the_file_as_a_whole_has_errors()
    {
        var result = Parse("""{"mcpServers": {"good": {"url": "https://a"}, "bad": {"url": "nope"}}}""");

        Assert.Equal(["good"], result.Entries.Select(e => e.Name));
        Assert.True(result.HasErrors);   // the engine freezes the bot's MCP state until the file is fixed
    }

    [Fact]
    public void The_same_name_twice_is_an_error_when_the_json_parser_lets_it_through()
    {
        var result = Parse("""{"mcpServers": {"x": {"url": "https://a"}, "x": {"url": "https://b"}}}""");

        Assert.True(result.HasErrors);
        Assert.Contains(result.Issues, i => i.Message.Contains("appears twice"));
        Assert.Single(result.Entries);
    }

    [Theory]
    [InlineData("""{ not json""", "malformed")]
    [InlineData("""[1,2]""", "top level must be an object")]
    [InlineData("""{"mcpServers": []}""", "Must be an object")]
    public void A_file_that_is_not_the_expected_shape_has_one_error_and_no_entries(string json, string expected)
    {
        var result = Parse(json);

        Assert.Empty(result.Entries);
        Assert.Contains(expected, Assert.Single(result.Issues).Message);
        Assert.True(result.HasErrors);
    }

    [Fact]
    public void A_long_literal_header_value_is_a_warning_not_an_error_and_a_secret_reference_is_not_one()
    {
        var literal = Parse("""{"mcpServers": {"x": {"url": "https://a", "headers": {"Authorization": "Bearer abcdefghijklmnopqrstuvwxyz"}}}}""");
        var reference = Parse("""{"mcpServers": {"x": {"url": "https://a", "headers": {"Authorization": "Bearer {{CRM_KEY_THAT_IS_LONG}}"}}}}""");
        var short_ = Parse("""{"mcpServers": {"x": {"url": "https://a", "headers": {"X-Tenant": "acme"}}}}""");

        Assert.False(literal.HasErrors);
        Assert.Single(literal.Entries);                                   // still used
        var warning = Assert.Single(literal.Issues);
        Assert.True(warning.IsWarning);
        Assert.Contains("{{KEY}}", warning.Message);
        Assert.Empty(reference.Issues);
        Assert.Empty(short_.Issues);
    }
}
