using EmailServer.Models;
using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Options;
using System.Text;

namespace EmailServer.Services
{
    public class EmailSender : IEmailSender
    {
        private readonly SmtpOptions _options;

        public EmailSender(IOptions<SmtpOptions> options)
        {
            _options = options.Value;
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
                using var smtp = new SmtpClient();
                await smtp.ConnectAsync(_options.Host, _options.Port, _options.UseSsl);
                if (!string.IsNullOrWhiteSpace(_options.Username))
                {
                    await smtp.AuthenticateAsync(_options.Username, _options.Password);
                }

                await smtp.SendAsync(message);
                await smtp.DisconnectAsync(true);
                return true;
            }
            catch
            {
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

                using var smtp = new SmtpClient();
                await smtp.ConnectAsync(_options.Host, _options.Port, _options.UseSsl);
                if (!string.IsNullOrWhiteSpace(_options.Username))
                {
                    await smtp.AuthenticateAsync(_options.Username, _options.Password);
                }

                await smtp.SendAsync(FormatOptions.Default, message, sender, recipients);
                await smtp.DisconnectAsync(true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
