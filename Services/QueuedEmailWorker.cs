using EmailServer.Data;
using EmailServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EmailServer.Services
{
    public class QueuedEmailWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly QueueWorkerOptions _options;
        private readonly ILogger<QueuedEmailWorker> _logger;

        public QueuedEmailWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<QueueWorkerOptions> options,
            ILogger<QueuedEmailWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.Enabled)
            {
                _logger.LogInformation("Queued email worker is disabled.");
                return;
            }

            _logger.LogInformation("Queued email worker started with batch size {BatchSize}.", _options.BatchSize);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessBatchAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Queued email worker failed while processing a batch.");
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(_options.PollIntervalSeconds, 1)), stoppingToken);
            }
        }

        private async Task ProcessBatchAsync(CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<EmailServerContext>();
            var sender = scope.ServiceProvider.GetRequiredService<IEmailSender>();
            var quotaService = scope.ServiceProvider.GetRequiredService<IQuotaService>();

            var now = DateTime.UtcNow;
            var messages = await db.QueuedEmails
                .Include(message => message.Tenant)
                .Where(message =>
                    (message.Status == QueuedEmailStatus.Queued || message.Status == QueuedEmailStatus.Deferred) &&
                    (message.NextAttemptAt == null || message.NextAttemptAt <= now))
                .OrderBy(message => message.CreatedAt)
                .Take(Math.Max(_options.BatchSize, 1))
                .ToListAsync(cancellationToken);

            foreach (var message in messages)
            {
                await ProcessMessageAsync(db, sender, quotaService, message, cancellationToken);
            }
        }

        private async Task ProcessMessageAsync(
            EmailServerContext db,
            IEmailSender sender,
            IQuotaService quotaService,
            QueuedEmail message,
            CancellationToken cancellationToken)
        {
            if (message.Tenant is null)
            {
                MarkFailed(message, "Tenant was not found.", null);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            var recipients = GetRecipients(message);
            if (recipients.Count == 0)
            {
                MarkFailed(message, "Queued message has no recipients.", null);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            if (!await quotaService.CanSendAsync(message.Tenant, recipients.Count))
            {
                Defer(message, "Tenant daily quota would be exceeded.", null, DateTime.UtcNow.AddHours(1));
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            message.Status = QueuedEmailStatus.Processing;
            message.DeliveryStatus = DeliveryStatus.Attempting;
            message.DeliveryDetails = $"Delivery attempt {message.AttemptCount + 1} of {Math.Max(_options.MaxAttempts, 1)} started.";
            message.LastDeliveryHost = null;
            message.AttemptCount += 1;
            message.LastAttemptAt = DateTime.UtcNow;
            message.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            var deliveryResult = await sender.SendQueuedAsync(message.Tenant, message);
            if (!deliveryResult.Success)
            {
                DateTime? nextAttempt = message.AttemptCount >= Math.Max(_options.MaxAttempts, 1)
                    ? null
                    : CalculateNextAttemptAt(message.AttemptCount);

                if (nextAttempt is null)
                {
                    MarkFailed(message, deliveryResult.Details, deliveryResult.Host);
                }
                else
                {
                    Defer(message, deliveryResult.Details, deliveryResult.Host, nextAttempt.Value);
                }

                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            message.Status = QueuedEmailStatus.Sent;
            message.DeliveryStatus = deliveryResult.Status;
            message.DeliveryDetails = deliveryResult.Details;
            message.LastDeliveryHost = deliveryResult.Host;
            message.SentAt = DateTime.UtcNow;
            message.NextAttemptAt = null;
            message.LastError = null;

            await db.SendEvents.AddRangeAsync(recipients.Select(recipient => new SendEvent
            {
                TenantId = message.TenantId,
                Recipient = recipient,
                SentAt = message.SentAt.Value
            }), cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Sent queued email {MessageId} for tenant {TenantId} to {RecipientCount} recipient(s).",
                message.Id,
                message.TenantId,
                recipients.Count);
        }

        private static List<string> GetRecipients(QueuedEmail message)
        {
            return message.RecipientsSerialized
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private DateTime CalculateNextAttemptAt(int attemptCount)
        {
            var initialDelay = TimeSpan.FromSeconds(Math.Max(_options.InitialRetryDelaySeconds, 1));
            var maxDelay = TimeSpan.FromMinutes(Math.Max(_options.MaxRetryDelayMinutes, 1));
            var multiplier = Math.Pow(2, Math.Max(attemptCount - 1, 0));
            var delaySeconds = Math.Min(initialDelay.TotalSeconds * multiplier, maxDelay.TotalSeconds);

            return DateTime.UtcNow.AddSeconds(delaySeconds);
        }

        private static void Defer(QueuedEmail message, string reason, string? host, DateTime nextAttemptAt)
        {
            message.Status = QueuedEmailStatus.Deferred;
            message.DeliveryStatus = DeliveryStatus.Deferred;
            message.DeliveryDetails = reason;
            message.LastDeliveryHost = host;
            message.NextAttemptAt = nextAttemptAt;
            message.LastError = reason;
        }

        private static void MarkFailed(QueuedEmail message, string reason, string? host)
        {
            message.Status = QueuedEmailStatus.Failed;
            message.DeliveryStatus = DeliveryStatus.Failed;
            message.DeliveryDetails = reason;
            message.LastDeliveryHost = host;
            message.NextAttemptAt = null;
            message.LastError = reason;
        }
    }
}
