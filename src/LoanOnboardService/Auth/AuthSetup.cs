using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace LoanOnboardService.Auth;

public static class Roles
{
    public const string Submit = "Loan.Submit";      // APS (service principal)
    public const string Read = "Loan.Read";          // dealer dashboard
    public const string Agent = "Loan.Agent";        // agent console: review queue
    public const string Approver = "Loan.Approver";  // may approve
}

public static class Policies
{
    public const string Submit = "Submit";
    public const string Read = "Read";
    public const string Agent = "Agent";
    public const string Approver = "Approver";
}

public static class ClaimsExtensions
{
    /// <summary>Custom claim that carries the dealer the caller acts for. Add it as an optional claim in Entra.</summary>
    public const string DealerClaim = "dealer_id";

    public static string? GetDealerId(this ClaimsPrincipal user) => user.FindFirst(DealerClaim)?.Value;

    public static string GetUserId(this ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value ?? user.FindFirst("sub")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity?.Name ?? "unknown";

    public static bool IsStaff(this ClaimsPrincipal user) =>
        user.IsInRole(Roles.Agent) || user.IsInRole(Roles.Approver);
}

public static class AuthSetup
{
    public static IServiceCollection AddLoanAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        if (env.IsDevelopment() && config.GetValue<bool>("Auth:DevBypass"))
        {
            // Local development only: roles and dealer come from request headers. Never enabled outside Development.
            services.AddAuthentication(DevAuthHandler.Scheme)
                .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.Scheme, _ => { });
        }
        else
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddMicrosoftIdentityWebApi(config.GetSection("AzureAd"));
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Submit, p => p.RequireRole(Roles.Submit))
            .AddPolicy(Policies.Read, p => p.RequireRole(Roles.Read, Roles.Submit, Roles.Agent, Roles.Approver))
            .AddPolicy(Policies.Agent, p => p.RequireRole(Roles.Agent, Roles.Approver))
            .AddPolicy(Policies.Approver, p => p.RequireRole(Roles.Approver));

        return services;
    }
}

/// <summary>
/// Development-only handler. Headers: X-Dev-User, X-Dev-Roles (comma separated), X-Dev-Dealer-Id.
/// </summary>
public class DevAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "DevBypass";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Dev-User"].FirstOrDefault() ?? "dev-user";
        var roles = (Request.Headers["X-Dev-Roles"].FirstOrDefault() ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var claims = new List<Claim> { new("oid", user), new(ClaimTypes.Name, user) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var dealer = Request.Headers["X-Dev-Dealer-Id"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(dealer)) claims.Add(new Claim(ClaimsExtensions.DealerClaim, dealer));

        var identity = new ClaimsIdentity(claims, Scheme);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}
