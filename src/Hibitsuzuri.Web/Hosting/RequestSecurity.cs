namespace Hibitsuzuri.Web.Hosting;

/// <summary>ページとAPIに共通するキャッシュ・ブラウザ保護・HTTPS制約を適用する</summary>
public static class RequestSecurity
{
    /// <summary>個人データをキャッシュしない既定値と、同一オリジンの資材だけを使う制約を設定する</summary>
    public static async Task AddHeadersAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; "
            + "connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'none'; "
            + "frame-ancestors 'none'; form-action 'self'";

        await next(context);
    }

    /// <summary>本番のHTTP閲覧を公開URLへ移し、平文で届いた更新要求は実行しない</summary>
    public static async Task RequireHttpsAsync(HttpContext context, RequestDelegate next)
    {
        var environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
        if (context.Request.IsHttps || environment.IsDevelopment())
        {
            await next(context);
            return;
        }

        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            var site = context.RequestServices.GetRequiredService<SiteSettings>();
            // 入力されたHostではなく、検証済みの公開先へ転送する。
            var location = site.PublicOrigin.GetLeftPart(UriPartial.Authority)
                + context.Request.PathBase.ToUriComponent()
                + context.Request.Path.ToUriComponent()
                + context.Request.QueryString.ToUriComponent();
            context.Response.Redirect(location, permanent: true, preserveMethod: true);
            return;
        }

        await Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "HTTPSで接続してください。").ExecuteAsync(context);
    }
}
