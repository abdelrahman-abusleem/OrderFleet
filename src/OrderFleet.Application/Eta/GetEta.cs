using OrderFleet.Application.Clock;
using OrderFleet.Application.Errors;
using OrderFleet.Application.Identity;
using OrderFleet.Application.Orders;

namespace OrderFleet.Application.Eta;

public interface IEtaStore
{
    Task<EtaInputs?> ReadAsync(Guid orderId, CancellationToken cancellationToken);
}

public sealed class GetEta(GetOrder orders, IEtaStore store, EtaCalculator calculator, IClock clock)
{
    public async Task<EtaEstimate> ExecuteAsync(Guid orderId, Actor actor, CancellationToken cancellationToken)
    {
        _ = await orders.ExecuteAsync(orderId, actor, cancellationToken) ?? throw new ResourceNotFoundException();
        var inputs = await store.ReadAsync(orderId, cancellationToken) ?? throw new ResourceNotFoundException();
        return calculator.Calculate(inputs, clock.UtcNow);
    }
}
