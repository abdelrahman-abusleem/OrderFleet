using Microsoft.EntityFrameworkCore;
using OrderFleet.Application.Dispatch;
using OrderFleet.Domain.Couriers;
using OrderFleet.Domain.Orders;
using OrderFleet.Infrastructure.Persistence;
using static OrderFleet.IntegrationTests.CatalogOrderingApiTests;
using static OrderFleet.IntegrationTests.DispatchConcurrencyTests;

namespace OrderFleet.IntegrationTests;

[Trait("Category", "PostgreSQL")]
public sealed class OrderingTransactionTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("Release", OrderStatus.Accepted, "Pending")]
    [InlineData("Cancel", OrderStatus.Cancelled, "Completed")]
    [InlineData("Deliver", OrderStatus.Delivered, "Completed")]
    public async Task AssignmentEffects_RollBackWithCallerOwnedTransaction(string action, OrderStatus expectedStatus, string expectedJobStatus)
    {
        await using var factory = new ApiFactory(fixture);
        using var courierClient = await CourierAsync(factory, 65);
        using var owner = await ClientAsync(factory, "RestaurantOwner");
        var (restaurant, product) = await CatalogForDispatchAsync(owner, 65);
        var id = await AcceptedAsync(factory, owner, restaurant, product);
        Assert.Equal(DispatchOutcome.Assigned, await DispatchAsync(factory, id));

        await using var before = fixture.CreateContext();
        var original = await before.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
        var originalJob = await before.DispatchJobs.AsNoTracking().SingleAsync(x => x.OrderId == id);
        var eventIds = await before.OutboxMessages.Where(x => x.AggregateId == id).Select(x => x.Id).ToArrayAsync();
        var auditCount = await before.OrderAuditEntries.CountAsync(x => x.OrderId == id);
        var assignmentId = original.AssignmentId!.Value;
        var courierId = original.CourierId!.Value;

        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var order = await context.Orders.SingleAsync(x => x.Id == id);
            var now = DateTimeOffset.UtcNow;
            switch (action)
            {
                case "Release": order.ReleaseAssignment(assignmentId, now); break;
                case "Cancel": order.Cancel(CancellationReason.AdminCancelled, now); break;
                case "Deliver":

                    order.MarkPickedUp(courierId, assignmentId, now);
                    order.Deliver(courierId, assignmentId, now);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
            var pendingEventIds = order.DomainEvents.Select(x => x.EventId).ToArray();
            var store = new EfOrderingStore(context);
            store.RecordAudit(courierId, id, action, now);
            await store.SaveAsync(CancellationToken.None);

            Assert.Equal(expectedStatus, (await context.Orders.AsNoTracking().SingleAsync(x => x.Id == id)).Status);
            var courier = await context.Couriers.AsNoTracking().SingleAsync(x => x.Id == courierId);
            Assert.Equal(CourierStatus.Available, courier.Status);
            Assert.Null(courier.OrderId);
            Assert.Null(courier.AssignmentId);
            var job = await context.DispatchJobs.AsNoTracking().SingleAsync(x => x.OrderId == id);
            Assert.Equal(expectedJobStatus, job.Status);
            Assert.Equal(action == "Release" ? (Guid?)null : assignmentId, job.AssignmentId);
            var history = await context.CourierAssignments.AsNoTracking().SingleAsync(x => x.Id == assignmentId);
            Assert.Equal(action == "Deliver", history.PickedUpAtUtc.HasValue);
            Assert.Equal(action == "Deliver", history.DeliveredAtUtc.HasValue);
            Assert.Equal(action != "Deliver", history.ReleasedAtUtc.HasValue);
            Assert.Equal(eventIds.Length + pendingEventIds.Length, await context.OutboxMessages.CountAsync(x => x.AggregateId == id));
            Assert.Equal(pendingEventIds.Length, await context.OutboxMessages.CountAsync(x => pendingEventIds.Contains(x.Id)));
            Assert.Equal(auditCount + 1, await context.OrderAuditEntries.CountAsync(x => x.OrderId == id));
            Assert.Equal(pendingEventIds, order.DomainEvents.Select(x => x.EventId));
            await transaction.RollbackAsync();
        }

        await using var verify = fixture.CreateContext();
        var restored = await verify.Orders.SingleAsync(x => x.Id == id);
        Assert.Equal(OrderStatus.Assigned, restored.Status);
        Assert.Equal(courierId, restored.CourierId);
        Assert.Equal(assignmentId, restored.AssignmentId);
        Assert.Null(restored.PickedUpAt);
        Assert.Null(restored.DeliveredAt);
        Assert.Null(restored.CancelledAt);
        var restoredCourier = await verify.Couriers.SingleAsync(x => x.Id == courierId);
        Assert.Equal(CourierStatus.Reserved, restoredCourier.Status);
        Assert.Equal(id, restoredCourier.OrderId);
        Assert.Equal(assignmentId, restoredCourier.AssignmentId);
        var restoredJob = await verify.DispatchJobs.SingleAsync(x => x.OrderId == id);
        Assert.Equal(originalJob.Status, restoredJob.Status);
        Assert.Equal(originalJob.AssignmentId, restoredJob.AssignmentId);
        Assert.Equal(originalJob.Attempts, restoredJob.Attempts);
        Assert.Equal(originalJob.AssignmentCount, restoredJob.AssignmentCount);
        Assert.Equal(originalJob.NextAttemptAtUtc, restoredJob.NextAttemptAtUtc);
        Assert.Equal(originalJob.CompletedAtUtc, restoredJob.CompletedAtUtc);
        var restoredHistory = await verify.CourierAssignments.SingleAsync(x => x.Id == assignmentId);
        Assert.Null(restoredHistory.PickedUpAtUtc);
        Assert.Null(restoredHistory.DeliveredAtUtc);
        Assert.Null(restoredHistory.ReleasedAtUtc);
        var restoredEventIds = await verify.OutboxMessages.Where(x => x.AggregateId == id).Select(x => x.Id).ToArrayAsync();
        Assert.Equal(eventIds.Order(), restoredEventIds.Order());
        Assert.Equal(auditCount, await verify.OrderAuditEntries.CountAsync(x => x.OrderId == id));
    }
}
