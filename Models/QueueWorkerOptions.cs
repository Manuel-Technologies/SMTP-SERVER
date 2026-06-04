namespace EmailServer.Models
{
    public class QueueWorkerOptions
    {
        public bool Enabled { get; set; } = true;
        public int PollIntervalSeconds { get; set; } = 10;
        public int BatchSize { get; set; } = 10;
        public int MaxAttempts { get; set; } = 5;
        public int InitialRetryDelaySeconds { get; set; } = 60;
        public int MaxRetryDelayMinutes { get; set; } = 60;
    }
}
