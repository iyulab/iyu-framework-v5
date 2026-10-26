using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Iyu.VaultAi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using Xunit;

namespace Iyu.Tests.VaultAi;

/// <summary>
/// The client sends a bearer token, so it has to know it is talking to the server it was pointed at. Against a
/// <b>real TLS listener</b> with a self-signed certificate (one no trust store vouches for): by default the connection
/// is refused; only the explicit opt-in accepts it.
/// </summary>
public sealed class VaultAiServerCertificateTests : IAsyncLifetime
{
    private WebApplication _app = default!;
    private X509Certificate2 _cert = default!;
    private string _url = default!;

    public async Task InitializeAsync()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        _cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), password: null);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, lo =>
        {
            lo.Protocols = HttpProtocols.Http1;
            lo.UseHttps(_cert);
        }));
        _app = builder.Build();
        _app.MapPost("/api/agents/{id}/messages", () => Results.Json(new
        {
            message = new { content = new[] { new { type = "text", text = "ok" } } },
        }));
        await _app.StartAsync();
        _url = _app.Urls.Single();
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        _cert.Dispose();
    }

    private VaultAiClient Client(bool allowUntrusted) => new(Options.Create(new VaultAiSettings
    {
        Url = _url,
        Token = "t",
        AllowUntrustedServerCertificate = allowUntrusted,
    }));

    [Fact]
    public async Task By_default_an_untrusted_server_certificate_is_refused()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => Client(allowUntrusted: false).GetMessageAsync(Guid.NewGuid(), "hi"));

        Assert.IsType<AuthenticationException>(ex.InnerException);
    }

    [Fact]
    public async Task The_opt_in_accepts_an_untrusted_server_certificate()
    {
        var text = await Client(allowUntrusted: true).GetMessageAsync(Guid.NewGuid(), "hi");

        Assert.Equal("ok", text);
    }

    [Fact]
    public void The_default_handler_carries_no_validation_override()
    {
        Assert.Null(VaultAiClient.CreateHandler(new VaultAiSettings()).ServerCertificateCustomValidationCallback);
        Assert.NotNull(VaultAiClient.CreateHandler(
            new VaultAiSettings { AllowUntrustedServerCertificate = true }).ServerCertificateCustomValidationCallback);
    }
}
