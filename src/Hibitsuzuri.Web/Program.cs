using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web;

/// <summary>MVCアプリの構成と起動処理</summary>
public partial class Program
{
    /// <summary>永続領域とDBの準備後にHTTPリクエストの受付を開始する</summary>
    public static async Task<int> Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (args is ["healthcheck"])
        {
            return await HealthCommand.RunAsync(builder.Configuration);
        }

        var site = new SiteSettings(builder.Configuration, builder.Environment);
        builder.Configuration["AllowedHosts"] = site.PublicOrigin.IdnHost;
        builder.Services.AddSingleton(site);
        builder.Services.AddSingleton<DataDirectory>();
        builder.Services.AddDbContext<AppDbContext>((services, options) =>
            options.UseSqlite(services.GetRequiredService<DataDirectory>().ConnectionString));
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddDataProtection().SetApplicationName("Hibitsuzuri");
        builder.Services.AddOptions<Microsoft.AspNetCore.DataProtection.KeyManagement.KeyManagementOptions>()
            .Configure<DataDirectory, ILoggerFactory>((options, data, loggerFactory) =>
            {
                options.XmlRepository = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(
                    new DirectoryInfo(data.KeysPath), loggerFactory);
            });

        builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.Name = builder.Environment.IsDevelopment()
                ? "Hibitsuzuri.Antiforgery" : "__Host-Hibitsuzuri.Antiforgery";
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
        });

        builder.Services.Configure<HostFilteringOptions>(options =>
        {
            options.AllowedHosts = [site.PublicOrigin.IdnHost];
            options.AllowEmptyHosts = false;
            options.IncludeFailureMessage = false;
        });
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var proxy in site.KnownProxies)
            {
                options.KnownProxies.Add(proxy);
            }
        });

        await using var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await DatabaseInitializer.InitializeAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), app.Lifetime.ApplicationStopping);
        }

        app.Use(RequestSecurity.AddHeadersAsync);
        app.Use(HealthEndpoints.HandleAsync);
        // 信頼先が空のまま転送処理を有効にすると、全接続元を信頼してしまう。
        if (site.KnownProxies.Count != 0)
        {
            app.UseForwardedHeaders();
        }

        app.UseHostFiltering();
        app.UseExceptionHandler("/error");
        app.UseStatusCodePagesWithReExecute("/error/{0}");
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.Use(RequestSecurity.RequireHttpsAsync);
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "public, max-age=3600"
        });
        app.MapControllers();

        var clock = app.Services.GetRequiredService<TimeProvider>();
        app.Logger.LogInformation("起動完了 TimeZone={TimeZone} LocalTime={LocalTime}", TimeZoneInfo.Local.Id, clock.GetLocalNow());
        await app.RunAsync();

        return 0;
    }
}
