using System.ComponentModel.DataAnnotations;

namespace Hibitsuzuri.Web.Models;

/// <summary>ブラウザのWebAuthn結果と対応する単回手続き</summary>
public sealed class PasskeyModel
{
    /// <summary>別タブの応答との取り違えを防ぐ手続きID</summary>
    [Required, StringLength(64)]
    public string ChallengeId { get; set; } = "";

    /// <summary>Identityで検証するWebAuthnのJSON</summary>
    [Required, StringLength(65536)]
    public string Credential { get; set; } = "";

    /// <summary>任意の表示名。未入力時は登録結果から提供元名を補完する</summary>
    [StringLength(200)]
    public string? Name
    {
        get; set;
    }
}
