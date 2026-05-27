using EmailServer.Models;

namespace EmailServer.Services
{
    public interface IEmailSender
    {
        Task<bool> SendAsync(Tenant tenant, EmailSendRequest request);
        Task<bool> SendQueuedAsync(Tenant tenant, QueuedEmail queuedEmail);
    }
}
