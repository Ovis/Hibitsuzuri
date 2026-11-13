using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Hibitsuzuri.Web.Auth;

/// <summary>単一ホスト内の認証変更と、所有者データの更新を直列化する</summary>
/// <remarks>ユーザー削除・停止の完了後に日記の書込みが確定しないよう、同じ排他権を使う</remarks>
public sealed class AuthWriteGate
{
    /// <summary>更新の排他権。DataDirectoryの排他でホストは一つに限定される</summary>
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
