namespace Hibitsuzuri.Web.Auth;

/// <summary>Identityの一時Cookieに、ブラウザ・用途・認証世代・単回使用を結び付ける</summary>
public sealed class PasskeyCeremonies(TimeProvider clock)
{
    private readonly Dictionary<string, PasskeyCeremony> _pending = [];

    /// <summary>ログアウト時に開始済みのPasskey手続きを取り消す</summary>
    public void Cancel(HttpContext context, SessionService sessions)
    {
        var name = sessions.CookieName("Ceremony");
        if (context.Request.Cookies[name] is { } token)
        {
            _pending.Remove(token);
        }

        context.Response.Cookies.Delete(name, sessions.CookieOptions(TimeSpan.Zero));
    }

    /// <summary>認証更新ロック内で、期限切れを回収して5分間の手続きを開始する</summary>
    public string? Begin(HttpContext context, SessionService sessions, PasskeyCeremony ceremony)
    {
        foreach (var id in _pending.Where(x => x.Value.ExpiresAt <= clock.GetUtcNow()).Select(x => x.Key).ToArray())
        {
            _pending.Remove(id);
        }

        var cookieName = sessions.CookieName("Ceremony");
        if (context.Request.Cookies[cookieName] is { } prior)
        {
            _pending.Remove(prior);
        }

        if (_pending.Count >= 128)
        {
            return null;
        }

        var token = AuthRules.Token();
        ceremony.ExpiresAt = clock.GetUtcNow().AddMinutes(5);
        _pending.Add(token, ceremony);

        context.Response.Cookies.Append(cookieName, token, sessions.CookieOptions(TimeSpan.FromMinutes(5)));

        return token;
    }

    /// <summary>ブラウザに一致した手続きを、成功・失敗によらず一度だけ消費する</summary>
    public PasskeyCeremony? Consume(HttpContext context, SessionService sessions, string id, string purpose)
    {
        var cookieName = sessions.CookieName("Ceremony");
        if (context.Request.Cookies[cookieName] != id)
        {
            return null;
        }

        context.Response.Cookies.Delete(cookieName, sessions.CookieOptions(TimeSpan.Zero));

        return _pending.Remove(id, out var ceremony) && ceremony.Purpose == purpose && ceremony.ExpiresAt > clock.GetUtcNow()
            ? ceremony : null;
    }
}

/// <summary>暗号検証はIdentityへ委ね、アプリの操作境界だけを保持する</summary>
public sealed class PasskeyCeremony
{
    /// <summary>login、register、reauthenticateの操作種別</summary>
    public required string Purpose
    {
        get; init;
    }

    /// <summary>開始時点で利用可能だった所有者と認証世代</summary>
    public required Dictionary<string, string?> UserStamps
    {
        get; init;
    }

    /// <summary>重要操作を開始した通常セッション</summary>
    public string? SessionId
    {
        get; init;
    }

    /// <summary>開始時に確定したログイン保持指定</summary>
    public bool RememberMe
    {
        get; init;
    }

    /// <summary>サーバーで検証する絶対期限</summary>
    public DateTimeOffset ExpiresAt
    {
        get; set;
    }
}
