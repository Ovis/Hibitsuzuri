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

    /// <summary>登録時に付ける端末・用途の名前</summary>
    [StringLength(200)]
    public string? Name
    {
        get; set;
    }
}
