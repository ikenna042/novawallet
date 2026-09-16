using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaWallet.Application;
using NovaWallet.Application.Abstractions;
using NovaWallet.Infrastructure.Persistence;

namespace NovaWallet.Infrastructure.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public int BatchSize { get; set; } = 50;
    public int MaxAttempts { get; set; } = 10;
}

/// <summary>
/// Publishes events that were committed to <c>outbox_messages</c> in the same transaction as the transfer,
/// so an event is never lost if the process dies after commit and never sent for a rolled-back transfer.
/// Delivery is at-least-once; consumers must de-duplicate on the event id.
/// <c>FOR UPDATE SKIP LOCKED</c> lets several API replicas run this safely side by side.
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IEventPublisher publisher,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var published = 0;
            try
            {
                published = await PublishBatchAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox batch failed");
            }

            if (published < settings.BatchSize)
                await Task.Delay(settings.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<int> PublishBatchAsync(OutboxOptions settings, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var batch = await db.Outbox
            .FromSql($"""
                      SELECT * FROM outbox_messages
                      WHERE processed_at IS NULL AND attempts < {settings.MaxAttempts}
                      ORDER BY occurred_at
                      LIMIT {settings.BatchSize}
                      FOR UPDATE SKIP LOCKED
                      """)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message.Type, message.Payload, ct);
                message.MarkProcessed(timeProvider.GetLedgerNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Publishing outbox message {MessageId} failed", message.Id);
                message.MarkFailed(ex.Message);
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return batch.Count;
    }
}

/// <summary>Stand-in for a real broker (Kafka / Azure Service Bus): writes the event to the structured log.</summary>
public sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(string eventType, string payload, CancellationToken ct)
    {
        logger.LogInformation("Published {EventType}: {Payload}", eventType, payload);
        return Task.CompletedTask;
    }
}
