using DnsClient;
using EmailServer.Data;
using EmailServer.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace EmailServer.Services
{
    public class DomainVerificationService : IDomainVerificationService
    {
        private readonly EmailServerContext _db;
        private readonly LookupClient _dnsClient;

        public DomainVerificationService(EmailServerContext db)
        {
            _db = db;
            _dnsClient = new LookupClient();
        }

        public async Task<DomainVerificationInfo?> GetVerificationInfoAsync(Guid tenantId)
        {
            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(tenant.VerificationToken))
            {
                InitializeVerificationData(tenant);
                await _db.SaveChangesAsync();
            }

            return BuildVerificationInfo(tenant);
        }

        public async Task<bool> VerifyDomainAsync(Guid tenantId)
        {
            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant is null)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(tenant.VerificationToken))
            {
                InitializeVerificationData(tenant);
                await _db.SaveChangesAsync();
            }

            var verification = await CheckTxtRecordAsync($"_verify.{tenant.Domain}", tenant.VerificationToken);
            if (!verification.Found)
            {
                return false;
            }

            tenant.DomainVerified = true;
            tenant.DomainVerifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return true;
        }

        public async Task<DomainAuthenticationStatus?> GetAuthenticationStatusAsync(Guid tenantId)
        {
            var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId);
            if (tenant is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(tenant.VerificationToken))
            {
                InitializeVerificationData(tenant);
                await _db.SaveChangesAsync();
            }

            var info = BuildVerificationInfo(tenant);
            var status = new DomainAuthenticationStatus
            {
                Domain = tenant.Domain,
                DomainVerified = tenant.DomainVerified,
                DomainVerifiedAt = tenant.DomainVerifiedAt,
                Verification = await CheckTxtRecordAsync(info.VerificationRecordName, info.VerificationRecordValue),
                Spf = await CheckTxtRecordAsync(info.SpfRecordName, info.SpfRecordValue),
                Dkim = await CheckTxtRecordAsync(info.DkimRecordName, info.DkimRecordValue),
                Dmarc = await CheckTxtRecordAsync(info.DmarcRecordName, info.DmarcRecordValue)
            };

            if (!tenant.DomainVerified && status.Verification.Found)
            {
                tenant.DomainVerified = true;
                tenant.DomainVerifiedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                status.DomainVerified = true;
                status.DomainVerifiedAt = tenant.DomainVerifiedAt;
            }

            status.ReadyToSendDirect =
                status.DomainVerified &&
                status.Spf.Found &&
                status.Dkim.Found &&
                status.Dmarc.Found;

            return status;
        }

        private static void InitializeVerificationData(Tenant tenant)
        {
            tenant.VerificationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            tenant.DkimSelector = "mail";

            using var rsa = RSA.Create(2048);
            var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            tenant.DkimPublicKey = publicKey;
            tenant.DkimPrivateKey = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        }

        private static DomainVerificationInfo BuildVerificationInfo(Tenant tenant)
        {
            var verificationName = $"_verify.{tenant.Domain}";
            var spfName = tenant.Domain;
            var dkimName = $"{tenant.DkimSelector}._domainkey.{tenant.Domain}";

            return new DomainVerificationInfo
            {
                Domain = tenant.Domain,
                Verified = tenant.DomainVerified,
                VerifiedAt = tenant.DomainVerifiedAt,
                VerificationRecordName = verificationName,
                VerificationRecordValue = tenant.VerificationToken,
                SpfRecordName = spfName,
                SpfRecordValue = "v=spf1 mx a -all",
                DkimRecordName = dkimName,
                DkimRecordValue = $"v=DKIM1; k=rsa; p={tenant.DkimPublicKey}",
                DmarcRecordName = $"_dmarc.{tenant.Domain}",
                DmarcRecordValue = "v=DMARC1; p=quarantine; adkim=s; aspf=s"
            };
        }

        private async Task<DnsRecordCheck> CheckTxtRecordAsync(string name, string expectedValue)
        {
            var check = new DnsRecordCheck
            {
                Name = name,
                ExpectedValue = expectedValue
            };

            try
            {
                var response = await _dnsClient.QueryAsync(name, QueryType.TXT);
                check.ActualValues = response.Answers
                    .TxtRecords()
                    .Select(record => string.Concat(record.Text).Trim())
                    .Where(record => !string.IsNullOrWhiteSpace(record))
                    .ToList();

                check.Found = check.ActualValues.Any(record =>
                    string.Equals(NormalizeTxt(record), NormalizeTxt(expectedValue), StringComparison.Ordinal));
            }
            catch (DnsResponseException ex)
            {
                check.Error = ex.Message;
            }

            return check;
        }

        private static string NormalizeTxt(string value)
        {
            return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }
}
