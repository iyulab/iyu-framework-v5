namespace Iyu.Tests.DocConvert;

/// <summary>
/// A hand-rolled <see cref="HttpMessageHandler"/> stub — this repo has no mocking library
/// dependency (<c>Iyu.Tests.csproj</c>), so outbound-HTTP tests intercept at this layer instead.
/// </summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return await responder(request, ct).ConfigureAwait(false);
    }
}
