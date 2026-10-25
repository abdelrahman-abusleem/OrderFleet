using OrderFleet.Application.Messaging;

namespace OrderFleet.Infrastructure.Messaging;

public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public long Sequence { get; private set; }
    public long? AggregateSequence { get; private set; }
    public string Type { get; private set; } = "";
    public int Version { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public Guid AggregateId { get; private set; }
    public string? CorrelationId { get; private set; }
    public string PayloadJson { get; private set; } = "";
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeasedUntilUtc { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }
    public string? LastErrorCode { get; private set; }

    private OutboxMessage() { }
    public OutboxMessage(IntegrationEvent message)
    {
        Id = message.MessageId; Type = message.Type; Version = message.Version;
        OccurredAtUtc = message.OccurredAtUtc; AggregateId = message.AggregateId;
        CorrelationId = message.CorrelationId; PayloadJson = message.PayloadJson;
        AggregateSequence = message.AggregateSequence;
        NextAttemptAtUtc = message.OccurredAtUtc;
    }
}

public sealed class InboxMessage
{
    public Guid MessageId { get; private set; }
    public string Consumer { get; private set; } = "";
    public DateTimeOffset ProcessedAtUtc { get; private set; }
    private InboxMessage() { }
    public InboxMessage(Guid messageId, string consumer, DateTimeOffset now)
    {
        MessageId = messageId; Consumer = consumer; ProcessedAtUtc = now.ToUniversalTime();
    }
}
