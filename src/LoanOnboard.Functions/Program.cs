using Azure.Identity;
using Azure.Messaging.ServiceBus;
using LoanOnboard.Core;
using LoanOnboard.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetryWorkerService();
builder.Services.ConfigureFunctionsApplicationInsights();

// ConnectionStrings__Sql and the Loan__* settings come from app settings (local.settings.json locally).
builder.Services.AddLoanCore(builder.Configuration);

// ServiceBusConnection is both the trigger binding connection and the sender connection.
// Use a connection string, or set ServiceBusConnection__fullyQualifiedNamespace to use managed identity instead.
builder.Services.AddSingleton(sp =>
{
    var config = builder.Configuration;
    var cs = config["ServiceBusConnection"];
    if (!string.IsNullOrWhiteSpace(cs) && !cs.StartsWith("REPLACE_", StringComparison.Ordinal))
        return new ServiceBusClient(cs);

    var ns = config["ServiceBusConnection__fullyQualifiedNamespace"];
    if (!string.IsNullOrWhiteSpace(ns))
        return new ServiceBusClient(ns, new DefaultAzureCredential());

    throw new InvalidOperationException(
        "Set ServiceBusConnection (connection string) or ServiceBusConnection__fullyQualifiedNamespace (managed identity).");
});
builder.Services.AddSingleton<OutboxPublisher>();

builder.Build().Run();
