using Microsoft.AspNetCore.Identity;

namespace Hibitsuzuri.Web.Auth;

/// <summary>標準Identityの認証情報と、利用可否・復旧の状態</summary>
public sealed class AppUser : IdentityUser
{
    /// <summary>次回以降の画像取り込みで原本を保持する設定</summary>
    public bool KeepOriginalImages
    {
        get; set;
    }

    /// <summary>日記の段落内で、行末の空白やバックスラッシュで明示した改行だけを表示する設定</summary>
    public bool RequireExplicitDiaryLineBreaks
    {
        get; set;
    }

    /// <summary>ユーザー管理を許可する権限。記録の所有権は付与しない</summary>
    public bool IsAdmin
    {
        get; set;
    }

    /// <summary>管理者による利用停止</summary>
    public bool IsSuspended
    {
        get; set;
    }

    /// <summary>初回または復旧後のコード保存確認が完了した状態</summary>
    public bool RecoveryCodesConfirmed
    {
        get; set;
    }

    /// <summary>パスワードと独立した復旧コードの連続失敗回数</summary>
    public int RecoveryFailures
    {
        get; set;
    }

    /// <summary>復旧手続きを再開できるUTC時刻（Unixミリ秒）</summary>
    public long RecoveryBlockedUntil
    {
        get; set;
    }
}

/// <summary>初回セットアップの消費状態と登録受付</summary>
public sealed class AccountPolicy
{
    /// <summary>唯一の設定行</summary>
    public int Id { get; set; } = 1;

    /// <summary>ユーザー件数が変わっても初回コードを再有効化しないための記録</summary>
    public bool SetupCompleted
    {
        get; set;
    }

    /// <summary>新規の自己登録を受け付ける状態</summary>
    public bool RegistrationEnabled { get; set; } = true;
}

/// <summary>Cookieに結び付くサーバー側の有効期限と失効状態</summary>
public sealed class AuthSession
{
    /// <summary>推測困難なセッション識別子</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>認証した所有者</summary>
    public string UserId { get; set; } = "";

    /// <summary>ログイン時の認証世代</summary>
    public string SecurityStamp { get; set; } = "";

    /// <summary>通知購読との関連付けにも使用するブラウザ識別子</summary>
    public string BrowserId { get; set; } = "";

    /// <summary>自動延長しない有効期限（Unixミリ秒）</summary>
    public long ExpiresAt
    {
        get; set;
    }

    /// <summary>最後に本人確認した時刻（Unixミリ秒）</summary>
    public long VerifiedAt
    {
        get; set;
    }

    /// <summary>明示ログアウト・強制失効の時刻。自然失効とは区別する</summary>
    public long? RevokedAt
    {
        get; set;
    }
}

/// <summary>復旧専用の単回使用コード。平文は保存しない</summary>
public sealed class RecoveryCode
{
    /// <summary>ユーザーとコードを含めたSHA-256ハッシュ</summary>
    public string Hash { get; set; } = "";

    /// <summary>コードを使用できる所有者</summary>
    public string UserId { get; set; } = "";
}

/// <summary>通常セッションから分離した短命の復旧・保存確認状態</summary>
public sealed class AccountFlow
{
    /// <summary>ブラウザに渡した乱数のハッシュ</summary>
    public string TokenHash { get; set; } = "";

    /// <summary>この手続きで確認済みの所有者</summary>
    public string UserId { get; set; } = "";

    /// <summary>手続き開始時の認証世代</summary>
    public string SecurityStamp { get; set; } = "";

    /// <summary>recover、confirm-login、confirm-recovery、confirm-codesのいずれか</summary>
    public string Purpose { get; set; } = "";

    /// <summary>手続きの絶対期限（Unixミリ秒）</summary>
    public long ExpiresAt
    {
        get; set;
    }

    /// <summary>保存確認後のログイン保持指定</summary>
    public bool RememberMe
    {
        get; set;
    }
}

/// <summary>秘密情報を含めない重要な認証・管理操作の履歴</summary>
public sealed class SecurityAuditLog
{
    /// <summary>監査イベントの識別子</summary>
    public long Id
    {
        get; set;
    }

    /// <summary>発生時刻（Unixミリ秒）</summary>
    public long OccurredAt
    {
        get; set;
    }

    /// <summary>固定されたイベント分類</summary>
    public string Event { get; set; } = "";

    /// <summary>操作実行者の内部ID。削除時にも保持する</summary>
    public string? ActorId
    {
        get; set;
    }

    /// <summary>対象者の内部ID。ログインIDや表示名は保存しない</summary>
    public string? SubjectId
    {
        get; set;
    }

    /// <summary>操作結果</summary>
    public bool Succeeded
    {
        get; set;
    }

    /// <summary>信頼するプロキシで解決済みの接続元</summary>
    public string? IpAddress
    {
        get; set;
    }

    /// <summary>長さを制限したブラウザ情報</summary>
    public string UserAgent { get; set; } = "";
}
