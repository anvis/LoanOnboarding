using LoanOnboard.Core.Common;
using LoanOnboard.Core.Data;
using LoanOnboard.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LoanOnboard.Core;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the DbContext and the shared services. Reads ConnectionStrings:Sql and the "Loan" section.
    /// </summary>
    public static IServiceCollection AddLoanCore(this IServiceCollection services, IConfiguration config)
    {
        var sql = config.GetConnectionString("Sql")
                  ?? throw new InvalidOperationException("Connection string 'Sql' (ConnectionStrings:Sql) is not configured.");

        services.Configure<LoanOptions>(config.GetSection(LoanOptions.SectionName));
        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<LoanDbContext>(o => o.UseSqlServer(sql, sqlOptions =>
            sqlOptions.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null)));

        services.AddScoped<LoanWorkflow>();
        services.AddScoped<OrchestratorService>();
        services.AddScoped<StageSweeper>();
        services.AddSingleton(sp => new OutboxRepository(sql, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoanOptions>>()));
        return services;
    }
}
