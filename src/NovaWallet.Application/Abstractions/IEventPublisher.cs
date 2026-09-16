namespace NovaWallet.Application.Abstractions;

/// <summary>Publishes an integration event to the outside world (a message broker in production).</summary>
public interface IEventPublisher
{
    Task PublishAsync(string eventType, string payload, CancellationToken ct);
}
