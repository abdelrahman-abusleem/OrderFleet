using OrderFleet.Domain.Payments;
using OrderFleet.Domain.Common;

namespace OrderFleet.Domain.Tests;

public sealed class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReconciledDecline_RequiresAnUncertainPayment_AndRecordsConfirmedFailure()
    {
        var payment = Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), 1m, "SAR", Now);
        Assert.Throws<DomainException>(() => payment.RecordReconciledDecline(Now));
        Assert.Equal(PaymentState.Authorized, payment.State);
        payment.RecordUncertainCapture();
        payment.RecordReconciledDecline(Now.AddSeconds(1));
        Assert.Equal(PaymentState.Failed, payment.State);
        Assert.Equal(Now.AddSeconds(1), payment.FailedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncertainCapture_RequiresReconciliation_AndCanResolveToCaptureOrVoid(bool captured)
    {
        var payment = Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), decimal.MaxValue, "SAR", Now);
        Assert.True(payment.RecordUncertainCapture());
        Assert.False(payment.RecordUncertainCapture());
        Assert.Equal(PaymentState.PendingReconciliation, payment.State);
        Assert.Throws<DomainException>(() => payment.RecordCaptureFailure(Now));
        Assert.Null(payment.FailedAt);
        Assert.Null(payment.CapturedAt);
        Assert.Equal(PaymentState.PendingReconciliation, payment.State);
        if (captured) Assert.True(payment.RecordCapture(Now.AddMinutes(1)));
        else Assert.True(payment.RecordVoid(Now.AddMinutes(1)));
        Assert.Equal(captured ? PaymentState.Captured : PaymentState.Voided, payment.State);
        Assert.Equal(decimal.MaxValue, payment.Amount);
    }

    [Fact]
    public void AuthorizedPayment_CapturesAndRefunds_Idempotently()
    {
        var order = Guid.NewGuid();
        var reference = Guid.NewGuid();
        var payment = Payment.Authorize(order, reference, 38.95m, "SAR", Now);
        Assert.Equal(order, payment.OrderId);
        Assert.Equal(reference, payment.AuthorizationId);
        Assert.Equal(38.95m, payment.Amount);
        Assert.Equal("SAR", payment.Currency);
        Assert.Equal(PaymentState.Authorized, payment.State);
        Assert.True(payment.RecordCapture(Now.AddMinutes(1)));
        Assert.False(payment.RecordCapture(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), payment.CapturedAt);
        Assert.True(payment.RecordRefund(Now.AddMinutes(3)));
        Assert.False(payment.RecordRefund(Now.AddMinutes(4)));
        Assert.Equal(PaymentState.Refunded, payment.State);
        Assert.Throws<DomainException>(() => payment.RecordCapture(Now.AddMinutes(5)));
        Assert.Throws<DomainException>(() => payment.RecordVoid(Now.AddMinutes(5)));
        Assert.Equal(PaymentState.Refunded, payment.State);
    }

    [Fact]
    public void FailedCapture_PreservesAuthorization_AndCanBeVoided()
    {
        var payment = Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), 10m, "SAR", Now);
        Assert.True(payment.RecordCaptureFailure(Now.AddMinutes(1)));
        Assert.False(payment.RecordCaptureFailure(Now.AddMinutes(2)));
        Assert.Equal(PaymentState.Failed, payment.State);
        Assert.True(payment.RecordVoid(Now.AddMinutes(3)));
        Assert.False(payment.RecordVoid(Now.AddMinutes(4)));
        Assert.Equal(PaymentState.Voided, payment.State);
        Assert.Throws<DomainException>(() => payment.RecordCapture(Now.AddMinutes(5)));
        Assert.Throws<DomainException>(() => payment.RecordRefund(Now.AddMinutes(5)));
        Assert.Equal(PaymentState.Voided, payment.State);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.001")]
    public void Authorization_RejectsInvalidMoney(string value) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), "SAR", Now));

    [Fact]
    public void Authorization_RejectsEmptyReferencesAndOtherCurrency()
    {
        Assert.Throws<ArgumentException>(() => Payment.Authorize(Guid.Empty, Guid.NewGuid(), 1m, "SAR", Now));
        Assert.Throws<ArgumentException>(() => Payment.Authorize(Guid.NewGuid(), Guid.Empty, 1m, "SAR", Now));
        Assert.Throws<ArgumentException>(() => Payment.Authorize(Guid.NewGuid(), Guid.NewGuid(), 1m, "USD", Now));
    }
}
