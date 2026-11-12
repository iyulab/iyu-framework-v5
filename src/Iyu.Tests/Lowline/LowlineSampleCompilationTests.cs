using Iyu.Lowline.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Iyu.Tests.Lowline;

/// <summary>
/// The README's "whole host" for <c>Iyu.Lowline</c> is the first thing a new user copies, so it is compiled here against
/// the packages as they are — read from the test output where <c>Iyu.Tests.csproj</c> copies the README, never from a copy
/// kept in test code.
/// </summary>
public class LowlineSampleCompilationTests
{
    private const string Marker = "<!-- lowline-sample -->";

    // The sample is written for a project with implicit usings, which new projects have; a plain compilation does not.
    private const string ImplicitUsings =
        "global using System;\nglobal using System.Collections.Generic;\nglobal using System.IO;\nglobal using System.Linq;\n"
        + "global using System.Threading;\nglobal using System.Threading.Tasks;\n";

    private static string Sample()
    {
        var match = Regex.Match(
            ReadmeCompilation.ReadmeText(),
            Regex.Escape(Marker) + @"\r?\n```csharp\r?\n(?<code>.*?)\r?\n```",
            RegexOptions.Singleline);
        Assert.True(match.Success,
            "the Iyu.Lowline sample was not found in the README (the code block that follows " + Marker + "). If the section "
            + "was reworded, keep the marker: this test asserts nothing while its subject is missing.");
        return match.Groups["code"].Value;
    }

    [Fact]
    public void The_whole_host_in_the_README_compiles_against_these_packages()
    {
        var errors = ReadmeCompilation.Errors("LowlineSample", ImplicitUsings + Sample())
            .Select(e => $"{e.Id}: {e.GetMessage()} ({e.Location.GetLineSpan().StartLinePosition.Line + 1})");

        Assert.Empty(errors);
    }

    /// <summary>
    /// Compiling proves the names exist, not that the host runs: the sample once asked for authentication and authorization
    /// middleware without registering either, compiled, and stopped on start. So the sample is built and started here, with
    /// only what a host brings itself added — a sign-in scheme and the one table row the hook reads.
    /// </summary>
    [Fact]
    public async Task The_whole_host_in_the_README_starts_and_answers()
    {
        const string Database = "Data Source=app.db";
        var file = Path.Combine(Path.GetTempPath(), $"iyu-lowline-sample-{Guid.NewGuid():N}.db");
        var source = ImplicitUsings + Sample();
        Assert.Contains(Database, source);   // the sample's own connection string; it is pointed at a temp file below
        var assembly = ReadmeCompilation.Load("LowlineSampleHost", source.Replace(Database, $"Data Source={file.Replace('\\', '/')}"));   // the path sits inside a string literal of the sample

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // The only thing the host adds is how callers sign in. Authorization is deliberately not registered here: the sample must.
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, LowlineTestAuthHandler>("Test", null);

        var app = (WebApplication)assembly.GetType("LowlineSample")!.GetMethod("Build")!
            .Invoke(null, [builder, """{"form":{"fields":[{"name":"customer","kind":"choice","choices":["acme","globex"]},{"name":"colour","kind":"choice","choices":["red","blue"]}]}}""", new string('s', 48)])!;
        try
        {
            await app.StartAsync();
            var orderId = Guid.NewGuid();
            using (var scope = app.Services.CreateScope())
            {
                var db = (DbContext)scope.ServiceProvider.GetRequiredService(assembly.GetType("AppDb")!);
                db.Database.EnsureCreated();
                var order = Activator.CreateInstance(assembly.GetType("Order")!)!;
                order.GetType().GetProperty("Id")!.SetValue(order, orderId);
                order.GetType().GetProperty("Customer")!.SetValue(order, "acme");
                order.GetType().GetProperty("Colour")!.SetValue(order, "red");
                db.Add(order);
                await db.SaveChangesAsync();
            }

            using var client = app.GetTestClient();
            HttpRequestMessage Propose(bool signedIn)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/lowline/order/propose")
                {
                    Content = new StringContent("{\"id\":\"" + orderId + "\",\"values\":{ }}", Encoding.UTF8, "application/json"),
                };
                if (signedIn) request.Headers.Add("X-Test-User", "alice");
                return request;
            }

            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Propose(signedIn: false))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Propose(signedIn: true))).StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public void The_sample_registers_the_authentication_it_uses()
    {
        // The start test lends the host a sign-in scheme through AddAuthentication, which would hide a sample that forgot it.
        var sample = Sample();

        Assert.Contains("AddAuthentication(", sample);
        Assert.Contains("AddAuthorization(", sample);
    }

    [Fact]
    public void The_sample_shows_the_opt_in_the_routes_and_a_hook()
    {
        var sample = Sample();

        Assert.Contains("AddIyuLowline<", sample);
        Assert.Contains("MapIyuLowline()", sample);
        Assert.Contains(": ILowlineFormHook", sample);
        Assert.Contains("ILowlineRecordRow", sample);
        Assert.Contains("ILowlineTuningRow", sample);
    }
}
