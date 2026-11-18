namespace Hibitsuzuri.Web.Hosting;

/// <summary>本文や例外メッセージを記録せず、発生箇所を識別する固定イベント</summary>
public enum OperationalEvent
{
    /// <summary>DB初期化を終えたアプリの起動</summary>
    Started = 1000,

    /// <summary>ログ保持期間の整理と書き込み状態の定期確認</summary>
    LogMaintenance = 1001,

    /// <summary>ログ保存先への書き込み失敗</summary>
    LogStorageFailed = 1002,

    /// <summary>期限切れの認証情報や監査ログの回収失敗</summary>
    AuthCleanupFailed = 1100,

    /// <summary>初回登録コードの読み取り・検証用ファイルの異常</summary>
    SetupTokenReadFailed = 1101,

    /// <summary>失効済み初回登録コードのファイル削除失敗</summary>
    SetupTokenCleanupFailed = 1102,

    /// <summary>不要になった画像の回収失敗</summary>
    ImageCleanupFailed = 1200,

    /// <summary>バックアップ転送用の一時ファイル回収失敗</summary>
    BackupCleanupFailed = 1300,

    /// <summary>取得・検証に失敗し、保存済み祝日データを維持</summary>
    HolidayUpdateFailed = 1400,

    /// <summary>祝日の更新状態を保存できず、次の周期で再確認</summary>
    HolidayStateFailed = 1401,

    /// <summary>通知の予約・送信処理の失敗</summary>
    PushProcessingFailed = 1500,

    /// <summary>準備完了判定に必要なDB検査の失敗</summary>
    DatabaseHealthFailed = 1600
}
