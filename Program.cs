using EmailServer.Data;
using EmailServer.Models;
using EmailServer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDbContext<EmailServerContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("EmailDatabase") ?? "Data Source=emails.db"));
builder.Services.AddScoped<ITenantService, TenantService>();
builder.Services.AddScoped<IEmailSender, EmailSender>();
builder.Services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
builder.Services.AddScoped<IQuotaService, QuotaService>();
builder.Services.AddScoped<IDomainVerificationService, DomainVerificationService>();
builder.Services.AddHostedService<SmtpSubmissionHostedService>();
builder.Services.AddHostedService<QueuedEmailWorker>();

builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.Configure<SmtpSubmissionOptions>(builder.Configuration.GetSection("SmtpSubmission"));
builder.Services.Configure<QueueWorkerOptions>(builder.Configuration.GetSection("QueueWorker"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EmailServerContext>();
    db.Database.EnsureCreated();
    EnsureQueuedEmailDeliveryStatusColumns(db);
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapPost("/api/tenants", async (TenantCreateRequest request, ITenantService service) =>
{
    var tenant = await service.CreateTenantAsync(request.Name, request.Domain, request.MaxMessagesPerDay);
    return Results.Created($"/api/tenants/{tenant.Id}", tenant);
});

app.MapGet("/api/tenants/{tenantId}", async (Guid tenantId, ITenantService service) =>
{
    var tenant = await service.GetTenantAsync(tenantId);
    return tenant is not null ? Results.Ok(tenant) : Results.NotFound();
});

app.MapGet("/api/tenants/{tenantId}/verification", async (Guid tenantId, IDomainVerificationService verificationService) =>
{
    var info = await verificationService.GetVerificationInfoAsync(tenantId);
    return info is not null ? Results.Ok(info) : Results.NotFound();
});

app.MapPost("/api/tenants/{tenantId}/verification/verify", async (Guid tenantId, IDomainVerificationService verificationService) =>
{
    var verified = await verificationService.VerifyDomainAsync(tenantId);
    return verified ? Results.Ok(new { status = "verified" }) : Results.BadRequest(new { status = "failed", message = "DNS record not found or token mismatch." });
});

app.MapPost("/api/send", async (EmailSendRequest request, HttpContext http, IApiKeyValidator apiKeyValidator, ITenantService tenantService, IEmailSender sender, IQuotaService quotaService, EmailServerContext db) =>
{
    if (request.To is null)
    {
        return Results.BadRequest(new { message = "At least one recipient is required." });
    }

    var recipients = request.To
        .Where(recipient => !string.IsNullOrWhiteSpace(recipient))
        .Select(recipient => recipient.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (recipients.Count == 0)
    {
        return Results.BadRequest(new { message = "At least one recipient is required." });
    }

    if (!http.Request.Headers.TryGetValue("X-API-Key", out var apiKey))
    {
        return Results.Unauthorized();
    }

    var tenant = await apiKeyValidator.ValidateAsync(apiKey.ToString());
    if (tenant is null)
    {
        return Results.Unauthorized();
    }

    if (!await quotaService.CanSendAsync(tenant, recipients.Count))
    {
        return Results.StatusCode(429);
    }

    var sendRequest = request with { To = recipients };

    var message = new EmailMessage
    {
        TenantId = tenant.Id,
        From = request.From,
        To = recipients,
        Subject = request.Subject,
        Body = request.Body,
        CreatedAt = DateTime.UtcNow
    };

    await db.EmailMessages.AddAsync(message);
    await db.SaveChangesAsync();

    var sendResult = await sender.SendAsync(tenant, sendRequest);
    if (!sendResult.Success)
    {
        return Results.Json(
            new { status = sendResult.Status, details = sendResult.Details, host = sendResult.Host },
            statusCode: StatusCodes.Status502BadGateway);
    }

    await quotaService.RecordSendAsync(tenant, message);
    return Results.Ok(new
    {
        message = "sent",
        tenantId = tenant.Id,
        messageId = message.Id,
        deliveryStatus = sendResult.Status,
        deliveryDetails = sendResult.Details,
        deliveryHost = sendResult.Host
    });
});

app.MapGet("/api/usage/{tenantId}", async (Guid tenantId, ITenantService service, IQuotaService quotaService) =>
{
    var tenant = await service.GetTenantAsync(tenantId);
    if (tenant is null)
    {
        return Results.NotFound();
    }

    var usage = await quotaService.GetUsageAsync(tenant);
    return Results.Ok(usage);
});

app.MapGet("/api/queue", async (EmailServerContext db) =>
{
    var messages = await db.QueuedEmails
        .OrderByDescending(message => message.CreatedAt)
        .Take(100)
        .Select(message => new
        {
            message.Id,
            message.TenantId,
            message.From,
            Recipients = message.RecipientsSerialized,
            QueueStatus = message.Status,
            message.DeliveryStatus,
            message.DeliveryDetails,
            message.LastDeliveryHost,
            message.AttemptCount,
            message.LastAttemptAt,
            message.NextAttemptAt,
            message.SentAt,
            message.LastError,
            message.CreatedAt
        })
        .ToListAsync();

    return Results.Ok(messages);
});

app.MapGet("/api/queue/{messageId}", async (Guid messageId, EmailServerContext db) =>
{
    var message = await db.QueuedEmails
        .Where(message => message.Id == messageId)
        .Select(message => new
        {
            message.Id,
            message.TenantId,
            message.From,
            Recipients = message.RecipientsSerialized,
            QueueStatus = message.Status,
            message.DeliveryStatus,
            message.DeliveryDetails,
            message.LastDeliveryHost,
            message.AttemptCount,
            message.LastAttemptAt,
            message.NextAttemptAt,
            message.SentAt,
            message.LastError,
            message.CreatedAt
        })
        .FirstOrDefaultAsync();

    return message is not null ? Results.Ok(message) : Results.NotFound();
});

app.MapGet("/api/tenants", async (ITenantService service) => await service.GetTenantsAsync());

app.Run();

static void EnsureQueuedEmailDeliveryStatusColumns(EmailServerContext db)
{
    ExecuteSchemaUpdateIfMissing(db, "ALTER TABLE QueuedEmails ADD COLUMN DeliveryStatus TEXT NOT NULL DEFAULT 'Pending'");
    ExecuteSchemaUpdateIfMissing(db, "ALTER TABLE QueuedEmails ADD COLUMN DeliveryDetails TEXT NULL");
    ExecuteSchemaUpdateIfMissing(db, "ALTER TABLE QueuedEmails ADD COLUMN LastDeliveryHost TEXT NULL");
}

static void ExecuteSchemaUpdateIfMissing(EmailServerContext db, string sql)
{
    try
    {
        db.Database.ExecuteSqlRaw(sql);
    }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
    {
    }
}
