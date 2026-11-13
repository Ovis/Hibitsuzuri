using System.Security.Claims;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Models;
using Hibitsuzuri.Web.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Hibitsuzuri.Web.Controllers;

/// <summary>Identity標準の暗号検証を用いたPasskey認証と本人の資格情報管理</summary>
[ServiceFilter<AuthOperationFilter>]
[EnableRateLimiting("authentication")]
[RequestSizeLimit(80000)]
[RequestFormLimits(ValueLengthLimit = 65536)]
public sealed class PasskeysController(AppDbContext db, UserManager<AppUser> users, SignInManager<AppUser> signIn,
    SessionService sessions, PasskeyCeremonies ceremonies, AuthAudit audit) : Controller
{
    /// <summary>公開鍵を露出させず、本人のPasskeyだけを画面へ表示する</summary>
    [HttpGet("/account/passkeys")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        return user is null ? Unauthorized() : View(await users.GetPasskeysAsync(user));
    }

    /// <summary>ユーザー名なしで認証できる選択肢を、開始時の認証世代に結び付ける</summary>
    [AllowAnonymous]
    [HttpPost("/api/passkeys/login/options")]
    public async Task<IActionResult> LoginOptions(bool rememberMe)
    {
        var stamps = await db.Users.Where(x => !x.IsSuspended && x.RecoveryCodesConfirmed).ToDictionaryAsync(x => x.Id, x => x.SecurityStamp);
        var id = ceremonies.Begin(HttpContext, sessions, new PasskeyCeremony { Purpose = "login", UserStamps = stamps, RememberMe = rememberMe });

        return id is null ? Failure(429) : Options(id, await signIn.MakePasskeyRequestOptionsAsync(null));
    }

    /// <summary>登録または再認証に必要な、本人のPasskey選択肢を発行する</summary>
    [HttpPost("/api/passkeys/{purpose}/options")]
    public async Task<IActionResult> Options(string purpose)
    {
        if (purpose is not ("register" or "reauthenticate") || (purpose == "register" && !await sessions.IsRecentAsync(User)))
        {
            return Failure(403);
        }

        var user = await users.GetUserAsync(User);
        if (user?.UserName is null)
        {
            return Failure(401);
        }

        if (purpose == "register" && (await users.GetPasskeysAsync(user)).Count >= 20)
        {
            return Failure(409);
        }

        var id = ceremonies.Begin(HttpContext, sessions, new PasskeyCeremony
        {
            Purpose = purpose,
            UserStamps = new() { [user.Id] = user.SecurityStamp },
            SessionId = User.FindFirstValue(SessionService.SessionClaim)
        });
        if (id is null)
        {
            return Failure(429);
        }

        var options = purpose == "register"
            ? await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity { Id = user.Id, Name = user.UserName, DisplayName = user.UserName })
            : await signIn.MakePasskeyRequestOptionsAsync(user);
        return Options(id, options);
    }

    /// <summary>単回の署名検証後に固定期限でログインする。パスワードの停止は適用しない</summary>
    [AllowAnonymous]
    [HttpPost("/api/passkeys/login/verify")]
    public async Task<IActionResult> Login(PasskeyModel model)
    {
        if (!ModelState.IsValid)
        {
            return Failure(400);
        }

        var ceremony = ceremonies.Consume(HttpContext, sessions, model.ChallengeId, "login");
        if (ceremony is null)
        {
            return Failure(400);
        }

        var assertion = await AssertAsync(model.Credential);
        if (assertion is null || !assertion.Succeeded || !Matches(ceremony, assertion.User)
            || !assertion.User.RecoveryCodesConfirmed || assertion.User.IsSuspended)
        {
            return Failure(401);
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        AuthRules.RequireSuccess(await users.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey));
        await sessions.SignInAsync(HttpContext, assertion.User, ceremony.RememberMe);
        await transaction.CommitAsync();

        return Ok(new
        {
            redirect = "/"
        });
    }

    /// <summary>ログイン期限を延長せず、同じユーザー・セッションの本人確認だけ更新する</summary>
    [HttpPost("/api/passkeys/reauthenticate/verify")]
    public async Task<IActionResult> Reauthenticate(PasskeyModel model)
    {
        if (!ModelState.IsValid)
        {
            return Failure(400);
        }

        var ceremony = ceremonies.Consume(HttpContext, sessions, model.ChallengeId, "reauthenticate");
        if (ceremony is null || ceremony.SessionId != User.FindFirstValue(SessionService.SessionClaim))
        {
            return Failure(400);
        }

        var assertion = await AssertAsync(model.Credential);
        if (assertion is null || !assertion.Succeeded || !Matches(ceremony, assertion.User)
            || assertion.User.Id != users.GetUserId(User))
        {
            return Failure(401);
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        AuthRules.RequireSuccess(await users.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey));
        await sessions.MarkVerifiedAsync(User);
        await transaction.CommitAsync();

        return Ok(new
        {
            redirect = "/account"
        });
    }

    /// <summary>本人確認と登録時の所有者を再照合し、既存資格情報を上書きせず追加する</summary>
    [HttpPost("/api/passkeys/register/verify")]
    public async Task<IActionResult> Register(PasskeyModel model)
    {
        if (!ModelState.IsValid || !AuthRules.ValidText(model.Name, 1, 100) || !await sessions.IsRecentAsync(User))
        {
            return Failure(400);
        }

        var user = await users.GetUserAsync(User);
        var ceremony = ceremonies.Consume(HttpContext, sessions, model.ChallengeId, "register");
        if (user is null || ceremony is null || !Matches(ceremony, user)
            || ceremony.SessionId != User.FindFirstValue(SessionService.SessionClaim))
        {
            return Failure(400);
        }

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await signIn.PerformPasskeyAttestationAsync(model.Credential);
        }
        catch (InvalidOperationException)
        {
            return Failure(400);
        }

        if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id)
        {
            return Failure(400);
        }

        if (await users.FindByPasskeyIdAsync(attestation.Passkey.CredentialId) is not null || (await users.GetPasskeysAsync(user)).Count >= 20)
        {
            return Failure(409);
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        attestation.Passkey.Name = model.Name;
        AuthRules.RequireSuccess(await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey));
        audit.Add(HttpContext, "passkey-added", user.Id);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new
        {
            redirect = "/account/passkeys"
        });
    }

    /// <summary>本人の表示名だけを変更する。所有者IDは入力から受け取らない</summary>
    [HttpPost("/account/passkeys/rename")]
    public async Task<IActionResult> Rename(string id, string name)
    {
        if (!AuthRules.ValidText(name, 1, 100))
        {
            return BadRequest("名前は1～100文字で入力してください。");
        }

        var user = await users.GetUserAsync(User);
        var passkey = user is null ? null : (await users.GetPasskeysAsync(user)).SingleOrDefault(x => WebEncoders.Base64UrlEncode(x.CredentialId) == id);
        if (user is null || passkey is null)
        {
            return NotFound();
        }

        passkey.Name = name;
        AuthRules.RequireSuccess(await users.AddOrUpdatePasskeyAsync(user, passkey));

        return RedirectToAction(nameof(Index));
    }

    /// <summary>対象を確認した上で、最近の本人確認を要求して削除する</summary>
    [HttpPost("/account/passkeys/delete")]
    public async Task<IActionResult> Delete(string id, bool confirmed)
    {
        if (!confirmed || !await sessions.IsRecentAsync(User))
        {
            return Failure(403);
        }

        var user = await users.GetUserAsync(User);
        var passkey = user is null ? null : (await users.GetPasskeysAsync(user)).SingleOrDefault(x => WebEncoders.Base64UrlEncode(x.CredentialId) == id);
        if (user is null || passkey is null)
        {
            return NotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        AuthRules.RequireSuccess(await users.RemovePasskeyAsync(user, passkey.CredentialId));
        audit.Add(HttpContext, "passkey-removed", user.Id);

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return RedirectToAction(nameof(Index));
    }

    /// <summary>一時Cookieがない場合を、暗号検証に進めない失敗として扱う</summary>
    private async Task<PasskeyAssertionResult<AppUser>?> AssertAsync(string credential)
    {
        try
        {
            return await signIn.PerformPasskeyAssertionAsync(credential);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>開始後に復旧・資格情報変更されたユーザーを拒否する</summary>
    private static bool Matches(PasskeyCeremony ceremony, AppUser user) => ceremony.UserStamps.TryGetValue(user.Id, out var stamp) && stamp == user.SecurityStamp;

    /// <summary>Identityが生成したJSONを保持してブラウザへ渡す</summary>
    private OkObjectResult Options(string id, string options) => Ok(new { challengeId = id, publicKeyOptions = JsonSerializer.Deserialize<JsonElement>(options) });

    /// <summary>認証器や秘密情報を含めない失敗応答</summary>
    private ObjectResult Failure(int status) => Problem(statusCode: status, title: "Passkeyの操作を完了できませんでした。本人確認や有効期限を確認して再試行してください。");
}
