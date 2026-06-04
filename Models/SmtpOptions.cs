namespace EmailServer.Models
{
    public class SmtpOptions
    {
        public string Host { get; set; } = "localhost";
        public int Port { get; set; } = 25;
        public bool UseSsl { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string DefaultFrom { get; set; } = "no-reply@example.com";
        public bool UseMxLookupDelivery { get; set; }
        public int MxPort { get; set; } = 25;
        public string LocalDomain { get; set; } = "localhost";
    }
}
