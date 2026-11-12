using System.ComponentModel.DataAnnotations;

namespace Hibitsuzuri.Web.Models;

/// <summary>登録画面の入力と受付状態</summary>
public sealed class RegisterModel
{
    /// <summary>変更不可のログインID</summary>
    [Required, StringLength(64)]
    public string LoginId { get; set; } = "";

    /// <summary>平文を再表示・保存しない新規パスワード</summary>
    [Required, StringLength(256), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    /// <summary>入力間違いを防ぐ確認欄</summary>
    [Compare(nameof(Password)), DataType(DataType.Password)]
    public string PasswordConfirmation { get; set; } = "";

    /// <summary>初回だけ必要なホスト設定のコード</summary>
    [StringLength(256), DataType(DataType.Password)]
    public string? SetupCode
    {
        get; set;
    }

    /// <summary>初回セットアップ画面として表示する状態</summary>
    public bool IsSetup
    {
        get; set;
    }
}

/// <summary>パスワードログインの入力</summary>
public sealed class LoginModel
{
    /// <summary>ログインID</summary>
    [Required, StringLength(64)]
    public string LoginId { get; set; } = "";

    /// <summary>保存や再表示をしないパスワード</summary>
    [Required, StringLength(256), DataType(DataType.Password)]
    public string Password { get; set; } = "";

    /// <summary>30日の固定期限を選ぶ指定</summary>
    public bool RememberMe
    {
        get; set;
    }

    /// <summary>同一サイト内に限定して検証する戻り先</summary>
    public string? ReturnUrl
    {
        get; set;
    }
}

/// <summary>一度限りのコード表示と保存確認の画面</summary>
public sealed class RecoveryCodesModel
{
    /// <summary>この応答限りのコード平文</summary>
    public string[] Codes { get; set; } = [];
}
