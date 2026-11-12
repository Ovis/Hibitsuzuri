using Hibitsuzuri.Web.Auth;

namespace Hibitsuzuri.Web.Models;

/// <summary>管理対象のアカウント一覧と登録受付状態</summary>
public sealed class UsersModel
{
    /// <summary>現在のページに表示するアカウント</summary>
    public required IReadOnlyList<AppUser> Users
    {
        get; init;
    }

    /// <summary>通常ユーザーの自己登録を受け付ける状態</summary>
    public bool RegistrationEnabled
    {
        get; init;
    }

    /// <summary>0を起点とする現在のページ</summary>
    public int Page
    {
        get; init;
    }

    /// <summary>次のページが存在する状態</summary>
    public bool HasMore
    {
        get; init;
    }
}
