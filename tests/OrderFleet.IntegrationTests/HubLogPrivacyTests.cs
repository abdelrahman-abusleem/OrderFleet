using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class HubLogPrivacyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task NegotiateAndWebSocketConnection_NeverLogQueryAccessToken()
    {
        var captured = new CapturedLogs();
        await using var factory = new ApiFactory(fixture);
        factory.CustomizeServices = services => services.AddSingleton<ILogEventSink>(captured);
        using var customer = await ClientAsync(factory, "Customer");
        var token = customer.DefaultRequestHeaders.Authorization!.Parameter!;
        var query = "?access_token=" + Uri.EscapeDataString(token);
        using var negotiate = await customer.PostAsync("/hubs/orders/negotiate" + query + "&negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(new Uri("wss://localhost/hubs/orders" + query), timeout.Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[1024];
        var handshake = await socket.ReceiveAsync(buffer, timeout.Token);
        Assert.Contains("{}", Encoding.UTF8.GetString(buffer, 0, handshake.Count));
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token);
        using var anonymous = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/orders/negotiate?access_token=invalid-private-token", null, timeout.Token)).StatusCode);
        Assert.NotEmpty(captured.Events);
        var logs = string.Join('\n', captured.Events);
        Assert.Contains("/hubs/orders/negotiate", logs);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain("invalid-private-token", logs);
        Assert.DoesNotContain("access_token=", logs);
    }
}
