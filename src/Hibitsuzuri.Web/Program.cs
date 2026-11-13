using System.Globalization;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Backups;
using Hibitsuzuri.Web.Calendar;
using Hibitsuzuri.Web.Diary;
using Hibitsuzuri.Web.Hosting;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Notifications;
using Hibitsuzuri.Web.Storage;
using Hibitsuzuri.Web.Tasks;
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
        if (args.Length > 0 && args[0] == "image-convert")
        {
            return ImageConverter.Run(args);
        }

        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
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
        builder.Services.AddSingleton<DiaryMarkdown>();
        builder.Services.AddScoped<CalendarReader>();
        builder.Services.AddScoped<TaskSchedule>();
        builder.Services.AddScoped<TaskReader>();
        builder.Services.AddScoped<BackupService>();
        builder.Services.AddScoped<BackupRestore>();
        builder.Services.AddHostedService<BackupMaintenance>();

        builder.Services.AddSingleton<PushKeys>();
        // 送信先URLには購読秘密が含まれるため、HTTPの自動リクエストログを使わない。
        builder.Services.AddSingleton<IPushTransport>(services => new PushTransport(services.GetRequiredService<PushKeys>(), site,
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) }));
        builder.Services.AddScoped<PushSubscriptions>();
        builder.Services.AddScoped<PushDispatcher>();
        builder.Services.AddHostedService<PushMaintenance>();

        builder.Services.AddScoped<ImageStore>();
        builder.Services.AddSingleton<ImageWorker>();
        builder.Services.AddHostedService<ImageMaintenance>();
        builder.AddAccountAuthentication(site);

        builder.Services.AddHttpClient<HolidaySource>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<HolidayUpdateGate>();
        builder.Services.AddScoped<HolidayUpdates>();
        builder.Services.AddHostedService<HolidayMaintenance>();

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
            OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = context.Context.Request.Path == "/sw.js"
                ? "no-cache" : "public, max-age=3600"
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers();

        var clock = app.Services.GetRequiredService<TimeProvider>();
        app.Logger.LogInformation("起動完了 TimeZone={TimeZone} LocalTime={LocalTime}", TimeZoneInfo.Local.Id, clock.GetLocalNow());
        await app.RunAsync();

        return 0;
    }
}
