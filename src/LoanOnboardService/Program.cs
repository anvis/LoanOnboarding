using Azure.Storage.Blobs;
using LoanOnboard.Core;
using LoanOnboard.Core.Data;
using LoanOnboardService.Auth;
using LoanOnboardService.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Connection strings come from configuration: appsettings.json locally, App Service settings in Azure.
builder.Services.AddLoanCore(builder.Configuration);
builder.Services.AddSingleton<BlobServiceClient>(_ => BlobClientFactory.Create(builder.Configuration));
builder.Services.AddSingleton<IDocumentStore, BlobDocumentStore>();
builder.Services.AddScoped<OnboardingService>();
builder.Services.AddScoped<ApprovalService>();

builder.Services.AddLoanAuth(builder.Configuration, builder.Environment);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 60_000_000);
// Reads APPLICATIONINSIGHTS_CONNECTION_STRING (set by the Bicep template). Does nothing useful when it is absent.
builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler(errors => errors.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogError(ex, "Unhandled exception");
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new { title = "Unexpected error", traceId = ctx.TraceIdentifier });
}));

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment()) app.MapOpenApi();

app.MapControllers();

// Liveness: the process is up. Readiness: it can reach the database.
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/health/ready", async (LoanDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503))
    .AllowAnonymous();

app.Run();

public partial class Program;
