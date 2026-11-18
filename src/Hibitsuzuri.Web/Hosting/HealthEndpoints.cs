using System.Net;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Hosting;

/// <summary>コンテナ内部の接続だけに稼働状態を返す</summary>
public static class HealthEndpoints
{
    /// <summary>転送ヘッダーの適用前に接続元を判定し、外部からのDB検査を防ぐ</summary>
    public static async Task HandleAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (path != "/health/live" && path != "/health/ready")
        {
            await next(context);
            return;
        }

        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        if (path == "/health/ready")
        {
            if (context.RequestServices.GetRequiredService<OperationalLogging>().FileFailed)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var database = context.RequestServices.GetRequiredService<AppDbContext>();
            try
            {
                if (!await database.Database.CanConnectAsync(context.RequestAborted)
                    || (await database.Database.GetPendingMigrationsAsync(context.RequestAborted)).Any())
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Hibitsuzuri.Web.Hosting.HealthEndpoints")
                    .LogError((int)OperationalEvent.DatabaseHealthFailed, exception, "DBの稼働確認に失敗しました。");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
        }

        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("OK", context.RequestAborted);
    }
}
