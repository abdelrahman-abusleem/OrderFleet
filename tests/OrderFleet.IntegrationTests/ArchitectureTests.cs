namespace OrderFleet.IntegrationTests;

public class ArchitectureTests
{
    [Fact]
    public void DomainAndApplication_HaveNoInfrastructureOrTransportDependencies()
    {
        var domain = typeof(OrderFleet.Domain.Orders.Order).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        var application = typeof(OrderFleet.Application.Orders.CreateOrder).Assembly.GetReferencedAssemblies().Select(x => x.Name!).ToArray();
        string[] forbidden = ["EntityFrameworkCore", "Npgsql", "RabbitMQ", "AspNetCore", "OrderFleet.Infrastructure", "OrderFleet.Api"];
        Assert.All(forbidden, prefix =>
        {
            Assert.DoesNotContain(domain, name => name.Contains(prefix, StringComparison.Ordinal));
            Assert.DoesNotContain(application, name => name.Contains(prefix, StringComparison.Ordinal));
        });
        Assert.Contains("OrderFleet.Domain", application);
        Assert.DoesNotContain(domain, name => name.StartsWith("OrderFleet.", StringComparison.Ordinal));
    }
}
