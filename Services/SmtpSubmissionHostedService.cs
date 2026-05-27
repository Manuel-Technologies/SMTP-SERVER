using EmailServer.Models;
using Microsoft.Extensions.Options;
using SmtpServer;
using SmtpServer.Authentication;

namespace EmailServer.Services
{
    public class SmtpSubmissionHostedService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SmtpSubmissionOptions _options;
        private readonly ILogger<SmtpSubmissionHostedService> _logger;

        public SmtpSubmissionHostedService(
            IServiceScopeFactory scopeFactory,
            IOptions<SmtpSubmissionOptions> options,
            ILogger<SmtpSubmissionHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var smtpOptions = new SmtpServerOptionsBuilder()
                .ServerName(_options.ServerName)
                .MaxMessageSize(_options.MaxMessageSize)
                .Endpoint(builder =>
                {
                    builder
                        .Port(_options.Port)
                        .AuthenticationRequired(_options.RequireAuthentication)
                        .AllowUnsecureAuthentication(_options.AllowUnsecureAuthentication);
                })
                .Build();

            var services = new SmtpServer.ComponentModel.ServiceProvider();
            services.Add((IUserAuthenticator)new SmtpApiKeyAuthenticator(_scopeFactory));

            var server = new SmtpServer.SmtpServer(smtpOptions, services);

            _logger.LogInformation(
                "SMTP submission server listening on port {Port} as {ServerName}. Authentication required: {AuthenticationRequired}",
                _options.Port,
                _options.ServerName,
                _options.RequireAuthentication);

            await server.StartAsync(stoppingToken);
        }
    }
}
