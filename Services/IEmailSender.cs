using EmailServer.Models;

namespace EmailServer.Services
{
    public interface IEmailSender
    {
        Task<DeliveryResult> SendAsync(Tenant tenant, EmailSendRequest request);
        Task<DeliveryResult> SendQueuedAsync(Tenant tenant, QueuedEmail queuedEmail);
    }
}
