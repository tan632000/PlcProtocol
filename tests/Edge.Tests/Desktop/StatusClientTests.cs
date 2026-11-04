using System.Net;
using System.Text;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.Edge.Tests.Desktop;

public class StatusClientTests
{
    internal const string MinimalStatus = """
        {"edgeId":"e1","nowUtc":"2026-11-03T10:00:00Z","pollIntervalMs":null,"lastCycleUtc":null,"readMs":null,"plc":null,
         "devices":[],"mqtt":{"host":"broker","port":8883,"connected":false},"bufferPending":0,"tags":[],
         "settingsApply":{"revision":"r","applyId":null,"state":"applied","error":null}}
        """;

    [Fact]
    public async Task OnlyRequest_IsGetApiStatusOnLoopbackIpv4()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(MinimalStatus)));
        using var client = new StatusClient(5099, handler);

        var result = await client.GetAsync(CancellationToken.None);

        Assert.Equal("e1", result.Status!.EdgeId);
        Assert.Null(result.FailureReason);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("http://127.0.0.1:5099/api/status", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task SlowAnswer_IsATimeout()
    {
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Json(MinimalStatus);
        });
        using var client = new StatusClient(5099, handler, TimeSpan.FromMilliseconds(50));

        var result = await client.GetAsync(CancellationToken.None);

        Assert.Null(result.Status);
        Assert.False(result.Cancelled);
        Assert.Equal("quá 2 giây không trả lời", result.FailureReason);
    }

    [Fact]
    public void DefaultTimeout_IsTwoSeconds() => Assert.Equal(TimeSpan.FromSeconds(2), StatusClient.DefaultTimeout);

    [Fact]
    public async Task ServerError_GivesTheHttpCode()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = new StatusClient(5099, handler);

        Assert.Equal("HTTP 500", (await client.GetAsync(CancellationToken.None)).FailureReason);
    }

    [Fact]
    public async Task RefusedConnection_IsNotConnected()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("refused", new System.Net.Sockets.SocketException(10061)));
        using var client = new StatusClient(5099, handler);

        Assert.Equal("không kết nối được", (await client.GetAsync(CancellationToken.None)).FailureReason);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task UnreadableBody_IsUnreadableData(string body)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(body)));
        using var client = new StatusClient(5099, handler);

        Assert.Equal("dữ liệu không đọc được", (await client.GetAsync(CancellationToken.None)).FailureReason);
    }

    [Fact]
    public async Task AnyOtherHandlerError_IsStillAFailure_NotAnException()
    {
        var handler = new RecordingHandler((_, _) => throw new InvalidOperationException("unexpected"));
        using var client = new StatusClient(5099, handler);

        Assert.Equal("không kết nối được", (await client.GetAsync(CancellationToken.None)).FailureReason);
    }

    [Fact]
    public async Task CallerCancellation_IsCancelled_NotAFailure()
    {
        using var cts = new CancellationTokenSource();
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Json(MinimalStatus);
        });
        using var client = new StatusClient(5099, handler);

        var result = await client.GetAsync(cts.Token);

        Assert.True(result.Cancelled);
        Assert.Null(result.FailureReason);
        Assert.Null(result.Status);
    }

    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return respond(request, cancellationToken);
        }
    }
}
