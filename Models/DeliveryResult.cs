namespace EmailServer.Models
{
    public record DeliveryResult(
        bool Success,
        string Status,
        string Details,
        string? Host = null)
    {
        public static DeliveryResult Delivered(string details, string? host = null) =>
            new(true, DeliveryStatus.Delivered, details, host);

        public static DeliveryResult Failed(string details, string? host = null) =>
            new(false, DeliveryStatus.Failed, details, host);
    }

    public static class DeliveryStatus
    {
        public const string Pending = "Pending";
        public const string Attempting = "Attempting";
        public const string Delivered = "Delivered";
        public const string Deferred = "Deferred";
        public const string Failed = "Failed";
    }
}
