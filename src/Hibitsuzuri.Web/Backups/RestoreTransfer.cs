namespace Hibitsuzuri.Web.Backups;

/// <summary>復元の所有者・有効期限・確定済み結果を保存し、応答喪失時の二重反映を防ぐ</summary>
public sealed class RestoreTransfer
{
    /// <summary>アップロード開始時に発行する推測困難な操作ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>アップロードと復元の本人</summary>
    public string UserId { get; set; } = "";

    /// <summary>アップロード中・確認待ちの一時ファイルを回収する日時</summary>
    public long ExpiresAt
    {
        get; set;
    }

    /// <summary>ZIPと画像の検証を最後まで完了した状態</summary>
    public bool Ready
    {
        get; set;
    }

    /// <summary>記録と同じトランザクションで確定した状態。再送は再適用しない</summary>
    public bool Completed
    {
        get; set;
    }
}
