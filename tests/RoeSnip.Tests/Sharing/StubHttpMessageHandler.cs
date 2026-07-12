using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RoeSnip.Tests.Sharing;

/// <summary>The one mock HttpMessageHandler every ProviderSpecShareProvider/ShareManager test in
/// this file wires an HttpClient to (per the track brief: "a mock HttpMessageHandler for the
/// pipeline, no network") - captures the single outgoing request (method, URL, headers, and the
/// request body as text - every built-in spec's request body is small/textual, so this never needs
/// to handle streaming) and hands back a caller-supplied canned response.</summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    /// <summary>Raw request body bytes, captured here (not read from LastRequest.Content after the
    /// fact) because ProviderSpecShareProvider disposes its HttpRequestMessage (and its Content) the
    /// moment UploadAsync returns - reading LastRequest.Content afterward throws
    /// ObjectDisposedException. Capturing during SendAsync (before disposal) sidesteps that.</summary>
    public byte[]? LastRequestBytes { get; private set; }

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public static StubHttpMessageHandler ReturningText(System.Net.HttpStatusCode status, string body) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });

    public static StubHttpMessageHandler Throwing(Exception exception) =>
        new(_ => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
        {
            LastRequestBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            LastRequestBody = System.Text.Encoding.UTF8.GetString(LastRequestBytes);
        }
        return _respond(request);
    }

    public HttpClient ToClient() => new(this);
}
