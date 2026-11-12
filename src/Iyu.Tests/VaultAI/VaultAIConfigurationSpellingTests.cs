using System.Net;
using Iyu.Tests.Server.OData;
using Iyu.VaultAI;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.VaultAI;

/// <summary>
/// The module's configuration section is documented as <c>VaultAI</c>. Hosts written before the spelling changed still carry
/// a <c>VaultAi</c> section; configuration keys are matched without regard to case, so they must keep working untouched.
/// </summary>
public sealed class VaultAIConfigurationSpellingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iyu-vaultai-spelling-" + Guid.NewGuid().ToString("N"));

    public VaultAIConfigurationSpellingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("VaultAi")]
    [InlineData("VaultAI")]
    [InlineData("vaultai")]
    public async Task A_section_spelled_in_any_case_configures_the_module(string section)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [section + ":Url"] = "https://vault.example.test",
            [section + ":Token"] = "server-key-123",
            [section + ":ChannelId"] = "chan-spelling",
            [section + ":ReportPath"] = _root,
        });
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, HeaderClaimAuthHandler>("Test", null);
        builder.Services.AddAuthorization();
        builder.Services.AddVaultAIReports(builder.Configuration);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapVaultAIEmbed();
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/vault-ai-embed/config");
        request.Headers.Add("X-Test-Perm", "anyone");
        using var response = await app.GetTestServer().CreateClient().SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("chan-spelling", body);
    }
}
