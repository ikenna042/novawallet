using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using NovaWallet.Api.Auth;
using NovaWallet.Api.Infrastructure;
using NovaWallet.Application;
using NovaWallet.Infrastructure;
using NovaWallet.Infrastructure.Auth;
using NovaWallet.Infrastructure.Persistence;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) =>
{
    logger.ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Service", "novawallet-ledger");

    if (context.Configuration.GetValue<bool>("Logging:UseJson"))
        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    else
        logger.WriteTo.Console(outputTemplate:
            "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {SourceContext}: {Message:lj}{NewLine}{Exception}");
});

builder.Services
    .AddLedgerApplication(builder.Configuration)
    .AddLedgerInfrastructure(builder.Configuration)
    .AddLedgerAuth(builder.Configuration)
    .AddLedgerRateLimiting(builder.Configuration);

builder.Services
    .AddControllers(o =>
    {
        o.Filters.Add<DomainExceptionFilter>();
        o.Filters.Add<ApiEnvelopeResultFilter>();
    })
    .AddJsonOptions(o =>
    {
        // Reject unknown fields (catches typos like "amount" instead of "amountKobo") and keep numbers strict,
        // so "100.50" or "100" (string) can never be coerced into a kobo amount.
        o.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        // Report which field is malformed without echoing internal .NET type names back to the caller.
        o.AllowInputFormatterExceptionMessages = false;
    });

// Every error, wherever it is produced, gets the { statusCode, message, data, ...RFC 7807 } shape. Writers are
// tried in registration order and AddControllers() already registered MVC's own writer, so ours goes first.
builder.Services.Insert(0, ServiceDescriptor.Singleton<IProblemDetailsWriter, EnvelopeProblemDetailsWriter>());
builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
    ApiEnvelope.Normalize(ctx.ProblemDetails);
    ctx.ProblemDetails.Extensions["traceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString()
                                               ?? ctx.HttpContext.TraceIdentifier;
    ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.GetCorrelationId();
});
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<LedgerDbContext>("database", tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "NovaWallet Ledger API",
        Version = "v1",
        Description = "Wallet ledger for FirstBank NovaPay. All amounts are integers in kobo (NGN). "
                      + "Register with POST /api/v1/auth/register, sign in with POST /api/v1/auth/login, "
                      + "then click Authorize and paste the accessToken. Admin endpoints need an admin account.",
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
    });
    var xml = Path.Combine(AppContext.BaseDirectory, "NovaWallet.Api.xml");
    if (File.Exists(xml))
        c.IncludeXmlComments(xml);
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}
await AdminSeeder.SeedAsync(app.Services);

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(o => o.EnrichDiagnosticContext = (diagnostics, http) =>
{
    // Log who and which endpoint, never request bodies (NDPA: keep personal data out of logs).
    diagnostics.Set("Subject", http.User.FindFirst(ClaimNames.Subject)?.Value);
    diagnostics.Set("ClientIp", http.Connection.RemoteIpAddress?.ToString());
});
app.UseExceptionHandler();
app.UseStatusCodePages();

// Swagger runs as middleware ahead of authorization, so the spec and UI are browsable without a token;
// every mapped endpoint below still requires one unless marked AllowAnonymous.
if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "NovaWallet Ledger API");
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

// Probes only look at the status code (200 / 503); the body uses the same envelope for humans.
static Task WriteHealth(HttpContext context, HealthReport report) =>
    context.Response.WriteAsJsonAsync(ApiEnvelope.Success(
        context.Response.StatusCode,
        report.Status.ToString(),
        new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        }));

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteHealth })
    .AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealth,
}).AllowAnonymous();

app.MapGet("/", () => Results.Redirect("/swagger")).AllowAnonymous().ExcludeFromDescription();

app.Run();

public partial class Program;
