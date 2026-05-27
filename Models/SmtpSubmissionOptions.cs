namespace EmailServer.Models
{
    public class SmtpSubmissionOptions
    {
        public string ServerName { get; set; } = "localhost";
        public int Port { get; set; } = 587;
        public bool RequireAuthentication { get; set; } = true;
        public bool AllowUnsecureAuthentication { get; set; } = true;
        public int MaxMessageSize { get; set; } = 10 * 1024 * 1024;
    }
}
