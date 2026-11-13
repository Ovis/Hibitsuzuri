using Hibitsuzuri.Web.Auth;
using Microsoft.AspNetCore.Identity;

namespace Hibitsuzuri.Web.Models;

/// <summary>設定画面にまとめて表示する本人のアカウントと管理先</summary>
public sealed class SettingsPage
{
    /// <summary>表示名と管理者向け導線を判断する現在のユーザー</summary>
    public required AppUser User
    {
        get; init;
    }

    /// <summary>本人が登録したパスキーの名前と管理先</summary>
    public required IList<UserPasskeyInfo> Passkeys
    {
        get; init;
    }

    /// <summary>選択した日記帳のゴミ箱。日記帳未作成なら管理画面</summary>
    public required string TrashPath
    {
        get; init;
    }
}
