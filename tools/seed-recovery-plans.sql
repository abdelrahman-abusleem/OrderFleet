-- Benchmark only: executed against a newly created Testcontainers database.
-- No production code uses this bulk historical dataset or bypasses the Domain API.
CREATE TEMP TABLE recovery_seed AS
SELECT i, md5('order-' || i)::uuid AS order_id, md5('authorization-' || i)::uuid AS authorization_id,
    md5('courier-' || i)::uuid AS courier_id, md5('assignment-' || i)::uuid AS assignment_id
FROM generate_series(1, 100000) i;

INSERT INTO "Orders" ("Id", "CustomerId", "RestaurantId", "Status", "CourierId", "AssignmentId", "CancelReason",
    "Currency", "TotalAmount", "CreatedAt", "AcceptedAt", "AssignedAt", "PickedUpAt", "DeliveredAt", "CancelledAt", "LastEventSequence")
SELECT order_id, md5('customer')::uuid, md5('restaurant')::uuid,
    CASE WHEN i <= 100 THEN 'Created' WHEN i <= 200 THEN 'Accepted' WHEN i <= 300 THEN 'Assigned'
        WHEN i <= 400 THEN 'Cancelled' WHEN i <= 500 THEN 'Created' ELSE 'Delivered' END,
    CASE WHEN i BETWEEN 201 AND 300 OR i > 500 THEN courier_id END,
    CASE WHEN i BETWEEN 201 AND 300 OR i > 500 THEN assignment_id END,
    CASE WHEN i BETWEEN 301 AND 400 THEN 'RestaurantCancelled' END,
    'SAR', 1, now() - interval '30 days',
    CASE WHEN i BETWEEN 101 AND 400 OR i > 500 THEN now() - interval '20 days' END,
    CASE WHEN i BETWEEN 201 AND 300 OR i > 500 THEN now() - interval '10 days' END,
    CASE WHEN i > 500 THEN now() - interval '9 days' END,
    CASE WHEN i > 500 THEN now() - interval '8 days' END,
    CASE WHEN i BETWEEN 301 AND 400 THEN now() - interval '10 days' END, 1
FROM recovery_seed;
INSERT INTO "OrderItems" ("OrderId", "ProductId", "Name", "UnitPrice", "Quantity", "TotalPrice")
SELECT order_id, md5('product')::uuid, 'Benchmark tea', 1, 1, 1 FROM recovery_seed;
INSERT INTO "OrderDeliveryInformation" ("OrderId", "Latitude", "Longitude", "AcceptanceDeadline")
SELECT order_id, 24, 46, now() + CASE WHEN i <= 100 THEN interval '-1 minute' ELSE interval '1 hour' END FROM recovery_seed;
INSERT INTO "DispatchJobs" ("OrderId", "Attempts", "AssignmentCount", "Status", "NextAttemptAtUtc", "CompletedAtUtc", "AssignmentId")
SELECT order_id, 1, CASE WHEN i BETWEEN 201 AND 300 OR i > 500 THEN 1 ELSE 0 END,
    CASE WHEN i <= 200 OR i BETWEEN 401 AND 500 THEN 'Pending' WHEN i <= 300 THEN 'Assigned' ELSE 'Completed' END,
    now() - interval '1 minute', CASE WHEN i BETWEEN 201 AND 400 OR i > 500 THEN now() - interval '1 day' END,
    CASE WHEN i BETWEEN 201 AND 300 OR i > 500 THEN assignment_id END FROM recovery_seed;
INSERT INTO "MockPaymentAuthorizations" ("Id", "ActorId", "Key", "Fingerprint", "Amount", "Currency", "State", "CreatedAt", "CapturedAt")
SELECT authorization_id, md5('customer')::uuid, 'benchmark-' || i, repeat('a', 64), 1, 'SAR',
    CASE WHEN i <= 100 THEN 'Authorized' ELSE 'Captured' END, now() - interval '30 days',
    CASE WHEN i > 100 THEN now() - interval '20 days' END FROM recovery_seed;
INSERT INTO "Payments" ("Id", "OrderId", "AuthorizationId", "Amount", "Currency", "State", "AuthorizedAt", "CapturedAt")
SELECT md5('payment-' || i)::uuid, order_id, authorization_id, 1, 'SAR',
    CASE WHEN i <= 100 THEN 'Authorized' WHEN i BETWEEN 401 AND 500 THEN 'PendingReconciliation' ELSE 'Captured' END,
    now() - interval '30 days', CASE WHEN i > 100 AND i NOT BETWEEN 401 AND 500 THEN now() - interval '20 days' END FROM recovery_seed;
INSERT INTO "MockPaymentAuthorizations" ("Id", "ActorId", "Key", "Fingerprint", "Amount", "Currency", "State", "CreatedAt")
SELECT md5('orphan-' || i)::uuid, md5('customer')::uuid, 'orphan-' || i, repeat('a', 64), 1, 'SAR', 'Authorized', now() - interval '1 day'
FROM generate_series(1, 100) i;
INSERT INTO "OutboxMessages" ("Id", "AggregateId", "AggregateSequence", "Type", "Version", "OccurredAtUtc", "PayloadJson", "PublishedAtUtc", "Attempts", "NextAttemptAtUtc")
SELECT md5('event-' || i)::uuid, order_id, 1,
    CASE WHEN i BETWEEN 301 AND 400 THEN 'order.cancelled.v1' ELSE 'order.created.v1' END,
    1, now() - interval '30 days', jsonb_build_object('orderId', order_id), now() - interval '10 days', 1, now() FROM recovery_seed;
INSERT INTO "AspNetUsers" ("Id", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
SELECT courier_id, false, false, false, false, 0 FROM recovery_seed;
INSERT INTO "Couriers" ("Id", "Status", "OrderId", "AssignmentId")
SELECT courier_id, CASE WHEN i BETWEEN 201 AND 300 THEN 'Reserved' ELSE 'Offline' END,
    CASE WHEN i BETWEEN 201 AND 300 THEN order_id END, CASE WHEN i BETWEEN 201 AND 300 THEN assignment_id END FROM recovery_seed;
INSERT INTO "CourierAssignments" ("Id", "OrderId", "CourierId", "AssignedAtUtc", "PickupDeadlineUtc", "PickedUpAtUtc", "DeliveredAtUtc")
SELECT assignment_id, order_id, courier_id, now() - interval '10 days', now() - interval '9 days',
    CASE WHEN i > 500 THEN now() - interval '9 days' END, CASE WHEN i > 500 THEN now() - interval '8 days' END
FROM recovery_seed WHERE i BETWEEN 201 AND 300 OR i > 500;
ANALYZE;
