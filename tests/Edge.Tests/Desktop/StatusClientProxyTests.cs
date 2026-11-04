using System.Globalization;
using System.Net;
using System.Net.Sockets;
using IndustrialIot.Edge.Tests.Fakes;
using IndustrialIot.EdgeDesktop.Core;

namespace IndustrialIot.Edge.Tests.Desktop;

/// <summary>Runs alone: it swaps <see cref="HttpClient.DefaultProxy"/>, which every HttpClient in the process reads.</summary>
[CollectionDefinition(nameof(DefaultProxyCollection), DisableParallelization = true)]
public sealed class DefaultProxyCollection;

[Collection(nameof(DefaultProxyCollection))]
public class StatusClientProxyTests
{
    [Fact]
    public async Task RealHandler_ReadsTheEdge_EvenWhenTheSystemProxyWouldTakeLoopback()
    {
        var driver = new FakePlcDriver();
        driver.SetRead("DB1.DBX0.0", true);
        var port = FreePort();
        await using var edge = EdgeTestHost.Create(
            new Dictionary<string, string?>
            {
                ["Edge:Monitor:Enabled"] = "true",
                ["Edge:Monitor:Port"] = port.ToString(CultureInfo.InvariantCulture),
                ["Edge:Tags:0:TagId"] = "Line1.Running",
                ["Edge:Tags:0:Address"] = "DB1.DBX0.0",
                ["Edge:Tags:0:DataType"] = "Bool",
            },
            services => services.UseDriver(driver),
            runAgentServices: true);
        await edge.Host.StartAsync();

        var previous = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = new DeadProxy();
        try
        {
            using var client = new StatusClient(port);

            var result = await client.GetAsync(CancellationToken.None);

            Assert.Null(result.FailureReason);
            Assert.Equal("test-edge", result.Status!.EdgeId);
        }
        finally
        {
            HttpClient.DefaultProxy = previous;
        }
    }

    /// <summary>A manual proxy without "bypass for local addresses", pointing at a port nothing listens on.</summary>
    private sealed class DeadProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => new("http://127.0.0.1:1");

        public bool IsBypassed(Uri host) => false;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
