using System.ComponentModel.DataAnnotations;

namespace EmailServer.Models
{
    public class QueuedEmail
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid TenantId { get; set; }
        public string From { get; set; } = string.Empty;
        public string RecipientsSerialized { get; set; } = string.Empty;
        public string RawMessage { get; set; } = string.Empty;
        public string Status { get; set; } = QueuedEmailStatus.Queued;
        public string DeliveryStatus { get; set; } = Models.DeliveryStatus.Pending;
        public string? DeliveryDetails { get; set; }
        public string? LastDeliveryHost { get; set; }
        public int AttemptCount { get; set; }
        public DateTime? LastAttemptAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
        public DateTime? SentAt { get; set; }
        public string? LastError { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public Tenant? Tenant { get; set; }
    }

    public static class QueuedEmailStatus
    {
        public const string Queued = "Queued";
        public const string Processing = "Processing";
        public const string Sent = "Sent";
        public const string Deferred = "Deferred";
        public const string Failed = "Failed";
    }
}
