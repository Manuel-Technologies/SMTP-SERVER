# Custom Multi-Tenant Email Server

This project is a starter C# ASP.NET Core email service with multi-tenant support, API key authentication, send quota tracking, and SMTP relay via MailKit.

## Features

- Multi-tenant email sending with tenant API keys
- Tenant creation and listing endpoints
- Send API protected by `X-API-Key`
- Quota tracking for daily messages
- SMTP relay or direct MX lookup delivery using `MailKit`
- SQLite persistence for tenants and send events

## Run locally

1. Install .NET 10 SDK
2. Open a terminal in `c:\Users\NWCS\Desktop\SMTP_SERVER`
3. Run:

```powershell
dotnet restore
dotnet run --project EmailServer.csproj
```

## Sample API usage

Create a tenant:

```http
POST /api/tenants
Content-Type: application/json

{
  "name": "Acme Corp",
  "domain": "acme.com",
  "maxMessagesPerDay": 500
}
```

Send email:

```http
POST /api/send
X-API-Key: <tenant-api-key>
Content-Type: application/json

{
  "from": "hello@acme.com",
  "to": ["user@example.com"],
  "subject": "Hello from Acme",
  "body": "This is a test email."
}
```

Get tenant usage:

```http
GET /api/usage/{tenantId}
```

## Notes

- Update SMTP settings in `appsettings.json` or environment variables
- The project creates `emails.db` automatically on startup
- Use `http://localhost:5000/swagger` to explore endpoints

## Outbound delivery

By default, outbound mail is sent through the configured SMTP relay:

```json
"Smtp": {
  "Host": "localhost",
  "Port": 25,
  "UseSsl": false,
  "Username": "",
  "Password": "",
  "DefaultFrom": "no-reply@example.com",
  "UseMxLookupDelivery": false,
  "MxPort": 25,
  "LocalDomain": "localhost"
}
```

Set `UseMxLookupDelivery` to `true` to deliver directly to recipient domains. The sender resolves each domain's MX records, tries hosts in preference order with STARTTLS when available, and falls back to the domain itself if no MX record is found.

## SMTP submission

The application also starts an authenticated SMTP submission listener. By default it listens on port `587` and uses the tenant API key as the SMTP password. The username is currently ignored.

Configure the listener in `appsettings.json`:

```json
"SmtpSubmission": {
  "ServerName": "localhost",
  "Port": 587,
  "RequireAuthentication": true,
  "AllowUnsecureAuthentication": true,
  "MaxMessageSize": 10485760
}
```

`AllowUnsecureAuthentication` is enabled for local development only. Production submission should use TLS before clients send credentials.

Accepted SMTP messages are stored in the `QueuedEmails` table with the raw RFC822 message, SMTP envelope sender, recipients, tenant id, and delivery status. Use the API to inspect the latest queued messages:

```http
GET /api/queue
```

Inspect one queued message:

```http
GET /api/queue/{messageId}
```

## Queue worker

A background worker polls queued SMTP submissions and delivers due messages through the configured outbound SMTP relay or direct MX delivery. It records successful sends in `SendEvents`, defers messages that would exceed tenant quota, and retries failed SMTP deliveries until `MaxAttempts` is reached.

Queued messages expose a queue status and a delivery status. Queue status tracks processing state: `Queued`, `Processing`, `Deferred`, `Sent`, or `Failed`. Delivery status tracks the current delivery outcome: `Pending`, `Attempting`, `Delivered`, `Deferred`, or `Failed`, with delivery details, last delivery host, attempt count, and next retry time.

Configure the worker in `appsettings.json`:

```json
"QueueWorker": {
  "Enabled": true,
  "PollIntervalSeconds": 10,
  "BatchSize": 10,
  "MaxAttempts": 5,
  "InitialRetryDelaySeconds": 60,
  "MaxRetryDelayMinutes": 60
}
```
