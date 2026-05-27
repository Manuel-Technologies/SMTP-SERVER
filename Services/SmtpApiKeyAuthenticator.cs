using SmtpServer;
using SmtpServer.Authentication;

namespace EmailServer.Services
{
    public class SmtpApiKeyAuthenticator : IUserAuthenticator
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public SmtpApiKeyAuthenticator(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<bool> AuthenticateAsync(ISessionContext context, string user, string password, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                return false;
            }

            using var scope = _scopeFactory.CreateScope();
            var apiKeyValidator = scope.ServiceProvider.GetRequiredService<IApiKeyValidator>();
            var tenant = await apiKeyValidator.ValidateAsync(password);

            return tenant is not null;
        }
    }
}
