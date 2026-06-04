using EmailServer.Models;
using MimeKit;

namespace EmailServer.Services
{
    public interface IDkimSigningService
    {
        void Sign(Tenant tenant, MimeMessage message);
    }
}
