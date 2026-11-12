using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Hibitsuzuri.Web.Auth;

/// <summary>単一ホスト内の認証変更と初回登録・最後の管理者の判定を直列化する</summary>
public sealed class AuthWriteGate
{
    /// <summary>認証更新の排他権。DataDirectoryの排他でホストは一つに限定される</summary>
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <summary>更新の待機中に失効した認証も、操作の実行前に拒否する</summary>
public sealed class AuthOperationFilter(AuthWriteGate gate, SessionService sessions) : IAsyncActionFilter
{
    /// <summary>POSTを直列化し、認証済み操作の有効性をロック取得後に再確認する</summary>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            await next();
            return;
        }

        await gate.Semaphore.WaitAsync(context.HttpContext.RequestAborted);
        try
        {
            if (context.HttpContext.User.Identity?.IsAuthenticated == true
                && await sessions.CurrentUserAsync(context.HttpContext.User) is null)
            {
                context.Result = new UnauthorizedResult();
                return;
            }

            await next();
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }
}
