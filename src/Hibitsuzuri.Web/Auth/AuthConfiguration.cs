using System.Threading.RateLimiting;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace Hibitsuzuri.Web.Auth;

/// <summary>Identityの標準ストアとアプリのセッション境界を構成する</summary>
public static class AuthConfiguration
{
    /// <summary>承認済みの認証方式・有効期限・入力制限を登録する</summary>
    public static void AddAccountAuthentication(this WebApplicationBuilder builder, SiteSettings site)
    {
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        builder.Services.AddIdentityCore<AppUser>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-";

            options.Password.RequiredLength = 1;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;

            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        }).AddEntityFrameworkStores<AppDbContext>().AddSignInManager().AddDefaultTokenProviders();

        builder.Services.AddScoped<IPasswordValidator<AppUser>, ScalarPasswordValidator>();
        builder.Services.AddScoped<SessionService>();
        builder.Services.AddScoped<RecoveryCodes>();
        builder.Services.AddScoped<AccountFlows>();
        builder.Services.AddScoped<PasswordAuthentication>();
        builder.Services.AddScoped<AccountRecoveryService>();
        builder.Services.AddScoped<AuthAudit>();
        builder.Services.AddScoped<AuthOperationFilter>();
        builder.Services.AddSingleton<AuthWriteGate>();
        builder.Services.AddSingleton<PasskeyCeremonies>();
        builder.Services.AddHostedService<AuthMaintenance>();

        builder.Services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<TimeProvider>((options, clock) =>
            {
                options.Cookie.Name = (builder.Environment.IsDevelopment() ? "" : "__Host-") + "Hibitsuzuri.Session";
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;

                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.TimeProvider = clock;
                options.LoginPath = "/auth/login";

                options.Events.OnValidatePrincipal = async context =>
                {
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<SessionService>();
                    if (context.Principal is null || await sessions.CurrentUserAsync(context.Principal) is null)
                    {
                        // 自然失効時は通知を止めないため、明示ログアウト処理を呼ばない。
                        context.RejectPrincipal();
                    }
                };
                options.Events.OnRedirectToLogin = context => AuthRedirectAsync(context, 401);
                options.Events.OnRedirectToAccessDenied = context => AuthRedirectAsync(context, 403);
            });

        builder.Services.Configure<IdentityPasskeyOptions>(options =>
        {
            options.ServerDomain = site.PublicOrigin.IdnHost;
            options.UserVerificationRequirement = "required";
            options.AuthenticatorTimeout = TimeSpan.FromMinutes(5);
            options.ValidateOrigin = context => ValueTask.FromResult(!context.CrossOrigin
                && context.Origin == site.PublicOrigin.GetLeftPart(UriPartial.Authority));
        });
        builder.Services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme)
            .Configure<TimeProvider>((options, clock) =>
            {
                options.Cookie.Name = (builder.Environment.IsDevelopment() ? "" : "__Host-") + "Hibitsuzuri.PasskeyState";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
                options.TimeProvider = clock;
            });

        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
        });
    }

    /// <summary>APIや更新要求ではHTML転送せず、入力を保持できる認証エラーを返す</summary>
    private static Task AuthRedirectAsync(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api") || !HttpMethods.IsGet(context.Request.Method) || status == 403)
        {
            return Results.Problem(statusCode: status, title: "認証を確認してください。").ExecuteAsync(context.HttpContext);
        }

        context.Response.Redirect(context.RedirectUri);

        return Task.CompletedTask;
    }
}
