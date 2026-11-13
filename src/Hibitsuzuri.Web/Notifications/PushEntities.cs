namespace Hibitsuzuri.Web.Notifications;

/// <summary>ユーザーとブラウザごとの通知許可・表示設定</summary>
public sealed class PushDevice
{
    /// <summary>購読の内部識別子</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>通知を受け取る所有者</summary>
    public string UserId { get; set; } = "";

    /// <summary>認証Cookieと共通のブラウザ識別子</summary>
    public string BrowserId { get; set; } = "";

    /// <summary>再有効化やアカウント切替前の配信を区別する世代</summary>
    public string Generation { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>同じPush購読を複数アカウントへ割り当てないための照合値</summary>
    public string EndpointHash { get; set; } = "";

    /// <summary>Data Protectionで保護した送信先と暗号鍵</summary>
    public string ProtectedSubscription { get; set; } = "";

    /// <summary>明示的な有効化状態。ログイン期限には連動しない</summary>
    public bool Enabled
    {
        get; set;
    }

    /// <summary>このブラウザだけでタスク名を表示する設定</summary>
    public bool ShowName
    {
        get; set;
    }

    /// <summary>再開より前の通知を送らないUTC境界</summary>
    public long EnabledSince
    {
        get; set;
    }

    /// <summary>テスト通知の連続送信を抑える最終実行時刻</summary>
    public long LastTestAt
    {
        get; set;
    }

    /// <summary>購読失効や配信失敗を設定画面へ伝える固定文</summary>
    public string? Error
    {
        get; set;
    }
}

/// <summary>各回・各設定・各ブラウザの送信と再試行を管理する</summary>
public sealed class PushDelivery
{
    /// <summary>再送でも変えない通知識別子</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>通知先のブラウザ</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>送信予約時の通知許可世代</summary>
    public string Generation { get; set; } = "";

    /// <summary>通常タスクまたは繰り返しの各回</summary>
    public string TaskId { get; set; } = "";

    /// <summary>変更されていない通知設定の識別子</summary>
    public string ReminderId { get; set; } = "";

    /// <summary>サーバーのタイムゾーンから変換した通知予定のUTC時刻</summary>
    public long ScheduledAt
    {
        get; set;
    }

    /// <summary>再試行または送信リースの期限</summary>
    public long NextAttemptAt
    {
        get; set;
    }

    /// <summary>Pushサービスが受付を返した時刻</summary>
    public long? SentAt
    {
        get; set;
    }

    /// <summary>送信結果不明も含む試行回数</summary>
    public int Attempts
    {
        get; set;
    }

    /// <summary>成功・期限切れ・無効化で以後送信しない</summary>
    public bool Finished
    {
        get; set;
    }

    /// <summary>操作中の端末へ送る固定文の確認通知</summary>
    public bool Test
    {
        get; set;
    }

    /// <summary>受信時に現在の許可を確認するための短命な能力トークン</summary>
    public string ProtectedToken { get; set; } = "";

    /// <summary>受信確認トークンの照合値</summary>
    public string TokenHash { get; set; } = "";
}
