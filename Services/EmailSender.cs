using EmailServer.Models;
using DnsClient;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Microsoft.Extensions.Options;
using System.Text;

namespace EmailServer.Services
{
    public class EmailSender : IEmailSender
    {
        private readonly SmtpOptions _options;
        private readonly ILogger<EmailSender> _logger;
        private readonly LookupClient _dnsClient;

        public EmailSender(IOptions<SmtpOptions> options, ILogger<EmailSender> logger)
        {
            _options = options.Value;
            _logger = logger;
            _dnsClient = new LookupClient();
        }

        public async Task<bool> SendAsync(Tenant tenant, EmailSendRequest request)
        {
            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(string.IsNullOrWhiteSpace(request.From) ? _options.DefaultFrom : request.From));
            message.To.AddRange(request.To.Select(MailboxAddress.Parse));
            message.Subject = request.Subject;
            message.Body = new TextPart("plain") { Text = request.Body };

            try
            {
                var sender = MailboxAddress.Parse(string.IsNullOrWhiteSpace(request.From) ? _options.DefaultFrom : request.From);
                var recipients = request.To.Select(MailboxAddress.Parse).ToList();
                return await DeliverAsync(message, sender, recipients);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send API email for tenant {TenantId}.", tenant.Id);
                return false;
            }
        }

        public async Task<bool> SendQueuedAsync(Tenant tenant, QueuedEmail queuedEmail)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(queuedEmail.RawMessage));
                var message = await MimeMessage.LoadAsync(stream);
                var recipients = queuedEmail.RecipientsSerialized
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(MailboxAddress.Parse)
                    .ToList();

                if (recipients.Count == 0)
                {
                    return false;
                }

                var sender = MailboxAddress.Parse(string.IsNullOrWhiteSpace(queuedEmail.From) ? _options.DefaultFrom : queuedEmail.From);

                return await DeliverAsync(message, sender, recipients);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send queued email {MessageId} for tenant {TenantId}.", queuedEmail.Id, tenant.Id);
                return false;
            }
        }

        private async Task<bool> DeliverAsync(MimeMessage message, MailboxAddress sender, List<MailboxAddress> recipients)
        {
            if (recipients.Count == 0)
            {
                return false;
            }

            if (_options.UseMxLookupDelivery)
            {
                return await DeliverByMxAsync(message, sender, recipients);
            }

            await SendViaRelayAsync(message, sender, recipients);
            return true;
        }

        private async Task SendViaRelayAsync(MimeMessage message, MailboxAddress sender, List<MailboxAddress> recipients)
        {
            using var smtp = new SmtpClient();
            smtp.LocalDomain = _options.LocalDomain;

            await smtp.ConnectAsync(_options.Host, _options.Port, _options.UseSsl);
            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await smtp.AuthenticateAsync(_options.Username, _options.Password);
            }

            await smtp.SendAsync(FormatOptions.Default, message, sender, recipients);
            await smtp.DisconnectAsync(true);
        }

        private async Task<bool> DeliverByMxAsync(MimeMessage message, MailboxAddress sender, List<MailboxAddress> recipients)
        {
            var recipientGroups = recipients
                .Where(recipient => !string.IsNullOrWhiteSpace(recipient.Domain))
                .GroupBy(recipient => recipient.Domain, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (recipientGroups.Count == 0)
            {
                return false;
            }

            foreach (var group in recipientGroups)
            {
                if (!await DeliverDomainGroupAsync(message, sender, group.Key, group.ToList()))
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> DeliverDomainGroupAsync(
            MimeMessage message,
            MailboxAddress sender,
            string domain,
            List<MailboxAddress> recipients)
        {
            var hosts = await GetMxHostsAsync(domain);
            foreach (var host in hosts)
            {
                try
                {
                    using var smtp = new SmtpClient();
                    smtp.LocalDomain = _options.LocalDomain;

                    await smtp.ConnectAsync(host, _options.MxPort, SecureSocketOptions.StartTlsWhenAvailable);
                    await smtp.SendAsync(FormatOptions.Default, message, sender, recipients);
                    await smtp.DisconnectAsync(true);

                    _logger.LogInformation(
                        "Delivered email directly to {Domain} through MX host {MxHost} for {RecipientCount} recipient(s).",
                        domain,
                        host,
                        recipients.Count);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MX delivery attempt to {MxHost} for {Domain} failed.", host, domain);
                }
            }

            return false;
        }

        private async Task<List<string>> GetMxHostsAsync(string domain)
        {
            try
            {
                var result = await _dnsClient.QueryAsync(domain, QueryType.MX);
                var hosts = result.Answers.MxRecords()
                    .OrderBy(record => record.Preference)
                    .Select(record => record.Exchange.Value.TrimEnd('.'))
                    .Where(host => !string.IsNullOrWhiteSpace(host))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (hosts.Count > 0)
                {
                    return hosts;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MX lookup for {Domain} failed; falling back to direct domain delivery.", domain);
            }

            return [domain];
        }
    }
}
