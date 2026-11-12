using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Models;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>管理者による登録受付とアカウントの管理。記録の閲覧権限は扱わない</summary>
[ServiceFilter<AuthOperationFilter>]
[EnableRateLimiting("authentication")]
public sealed class UsersController(AppDbContext db, UserManager<AppUser> users, SessionService sessions, AuthAudit audit) : Controller
{
    /// <summary>最新の管理者権限を照合して、アカウント一覧を表示する</summary>
    [HttpGet("/admin/users")]
    public async Task<IActionResult> Index(int page = 0)
    {
        if (!await IsAdminAsync())
        {
            return Forbid();
        }

        page = Math.Clamp(page, 0, 100000);
        var accounts = await db.Users.AsNoTracking().OrderBy(x => x.UserName).Skip(page * 50).Take(51).ToListAsync();
        return View(new UsersModel
        {
            Users = accounts.Take(50).ToArray(),
            Page = page,
            HasMore = accounts.Count > 50,
            RegistrationEnabled = await db.AccountPolicies.AnyAsync(x => x.RegistrationEnabled)
        });
    }

    /// <summary>新規登録だけを切り替え、既存ユーザーの利用を維持する</summary>
    [HttpPost("/admin/users/registration")]
    public async Task<IActionResult> Registration(bool enabled)
    {
        if (!await IsAdminAsync() || !await sessions.IsRecentAsync(User))
        {
            return Forbid();
        }

        var policy = await db.AccountPolicies.SingleAsync();
        policy.RegistrationEnabled = enabled;
        audit.Add(HttpContext, enabled ? "registration-opened" : "registration-closed", null);
        await db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>排他内で最後の管理者を保護し、権限変更・停止・削除を確定する</summary>
    [HttpPost("/admin/users/change")]
    public async Task<IActionResult> Change(string id, string operation, bool confirmed, string? confirmationName)
    {
        if (!await IsAdminAsync() || !await sessions.IsRecentAsync(User))
        {
            return Forbid();
        }

        if (!confirmed || operation is not ("grant" or "revoke" or "suspend" or "resume" or "delete"))
        {
            return BadRequest("対象と操作内容の確認が必要です。");
        }

        var target = await users.FindByIdAsync(id);
        if (target is null)
        {
            return NotFound();
        }

        if (operation == "delete" && !string.Equals(confirmationName, target.UserName, StringComparison.Ordinal))
        {
            return BadRequest("削除するユーザーIDを正確に入力してください。");
        }

        if (target.IsAdmin && !target.IsSuspended && operation is "revoke" or "suspend" or "delete"
            && await db.Users.CountAsync(x => x.IsAdmin && !x.IsSuspended) <= 1)
        {
            return Conflict("最後の利用可能な管理者は、権限解除・利用停止・削除できません。");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        await sessions.RevokeAsync(target.Id);
        await db.AccountFlows.Where(x => x.UserId == target.Id).ExecuteDeleteAsync();
        if (operation == "delete")
        {
            AuthRules.RequireSuccess(await users.DeleteAsync(target));
        }
        else
        {
            switch (operation)
            {
                case "grant":
                    target.IsAdmin = true;
                    break;
                case "revoke":
                    target.IsAdmin = false;
                    break;
                case "suspend":
                    target.IsSuspended = true;
                    break;
                case "resume":
                    target.IsSuspended = false;
                    break;
            }

            AuthRules.RequireSuccess(await users.UpdateSecurityStampAsync(target));
        }

        audit.Add(HttpContext, "user-" + operation, target.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Cookie内の古い権限ではなく、現在のDB上の管理者状態を調べる</summary>
    private async Task<bool> IsAdminAsync()
    {
        var id = users.GetUserId(User);
        return await db.Users.AnyAsync(x => x.Id == id && x.IsAdmin && !x.IsSuspended);
    }
}
