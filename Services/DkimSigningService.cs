using System.Text;
using EmailServer.Models;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Cryptography;

namespace EmailServer.Services
{
    public class DkimSigningService : IDkimSigningService
    {
        private readonly DkimOptions _options;
        private readonly ILogger<DkimSigningService> _logger;

        public DkimSigningService(IOptions<DkimOptions> options, ILogger<DkimSigningService> logger)
        {
            _options = options.Value;
            _logger = logger;
        }

        public void Sign(Tenant tenant, MimeMessage message)
        {
            if (!_options.Enabled || message.Headers.Contains(HeaderId.DkimSignature))
            {
                return;
            }

            if (!CanSignTenantDomain(tenant, message))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(tenant.DkimSelector) || string.IsNullOrWhiteSpace(tenant.DkimPrivateKey))
            {
                _logger.LogWarning("Tenant {TenantId} has no DKIM selector or private key configured.", tenant.Id);
                return;
            }

            try
            {
                using var privateKey = new MemoryStream(Encoding.ASCII.GetBytes(ToPemPrivateKey(tenant.DkimPrivateKey)));
                var signer = new DkimSigner(privateKey, tenant.Domain, tenant.DkimSelector, DkimSignatureAlgorithm.RsaSha256)
                {
                    HeaderCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                    BodyCanonicalizationAlgorithm = DkimCanonicalizationAlgorithm.Relaxed,
                    AgentOrUserIdentifier = $"@{tenant.Domain}"
                };

                if (_options.SignatureExpirationHours > 0)
                {
                    signer.SignaturesExpireAfter = TimeSpan.FromHours(_options.SignatureExpirationHours);
                }

                signer.Sign(message, GetHeadersToSign());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to DKIM sign message for tenant {TenantId} and domain {Domain}.", tenant.Id, tenant.Domain);
            }
        }

        private bool CanSignTenantDomain(Tenant tenant, MimeMessage message)
        {
            if (_options.RequireVerifiedDomain && !tenant.DomainVerified)
            {
                _logger.LogWarning("Skipping DKIM signing for unverified domain {Domain}.", tenant.Domain);
                return false;
            }

            var fromDomain = message.From.Mailboxes.FirstOrDefault()?.Domain;
            if (string.IsNullOrWhiteSpace(fromDomain))
            {
                _logger.LogWarning("Skipping DKIM signing because the message has no mailbox From domain.");
                return false;
            }

            var matchesTenantDomain =
                string.Equals(fromDomain, tenant.Domain, StringComparison.OrdinalIgnoreCase) ||
                fromDomain.EndsWith($".{tenant.Domain}", StringComparison.OrdinalIgnoreCase);

            if (!matchesTenantDomain)
            {
                _logger.LogWarning(
                    "Skipping DKIM signing because From domain {FromDomain} does not match tenant domain {Domain}.",
                    fromDomain,
                    tenant.Domain);
            }

            return matchesTenantDomain;
        }

        private List<string> GetHeadersToSign()
        {
            var headers = _options.HeadersToSign
                .Where(header => !string.IsNullOrWhiteSpace(header))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return headers.Count > 0 ? headers : ["From", "To", "Subject", "Date"];
        }

        private static string ToPemPrivateKey(string base64PrivateKey)
        {
            var trimmed = base64PrivateKey.Trim();
            if (trimmed.StartsWith("-----BEGIN", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var body = string.Join(Environment.NewLine, SplitEvery(trimmed, 64));
            return $"-----BEGIN PRIVATE KEY-----{Environment.NewLine}{body}{Environment.NewLine}-----END PRIVATE KEY-----{Environment.NewLine}";
        }

        private static IEnumerable<string> SplitEvery(string value, int length)
        {
            for (var i = 0; i < value.Length; i += length)
            {
                yield return value.Substring(i, Math.Min(length, value.Length - i));
            }
        }
    }
}
