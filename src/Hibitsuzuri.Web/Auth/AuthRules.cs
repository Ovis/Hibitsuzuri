using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Hibitsuzuri.Web.Auth;

/// <summary>認証入力の境界と、秘密を保存しない識別値の生成</summary>
public static class AuthRules
{
    /// <summary>不正なUTF-16とNULを拒否し、Unicodeスカラー値で文字数を数える</summary>
    public static bool ValidText(string? text, int min, int max)
    {
        if (text is null || text.Length > max * 2)
        {
            return false;
        }

        var remaining = text.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || rune.Value == 0)
            {
                return false;
            }

            count++;
            remaining = remaining[consumed..];
        }

        return count >= min && count <= max;
    }

    /// <summary>変更できないログインIDの許可範囲</summary>
    public static bool ValidLoginId(string? value) => value is { Length: >= 3 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>トークン・コードの検証用ハッシュ</summary>
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>URLとCookieに安全に載せられる256bitの乱数</summary>
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    /// <summary>Identityの内部更新失敗を成功として扱わず、トランザクションを中断する</summary>
    public static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("認証情報の更新を確定できませんでした。");
        }
    }
}

/// <summary>文字種を強制せず、パスワードのスカラー値数を検証する</summary>
public sealed class ScalarPasswordValidator : IPasswordValidator<AppUser>
{
    /// <summary>12～128文字の入力を補正せず検証する</summary>
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        return Task.FromResult(AuthRules.ValidText(password, 12, 128) ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError { Code = "PasswordLength", Description = "パスワードは12～128文字で入力してください。" }));
    }
}
