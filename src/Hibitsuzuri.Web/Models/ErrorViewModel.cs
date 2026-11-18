namespace Hibitsuzuri.Web.Models;

/// <summary>エラーの案内と調査用の識別情報</summary>
public sealed class ErrorViewModel
{
    /// <summary>応答のHTTPステータス</summary>
    public required int StatusCode
    {
        get; init;
    }

    /// <summary>利用者へ示す説明</summary>
    public required string Message
    {
        get; init;
    }

    /// <summary>サーバーログと照合する識別子</summary>
    public required string RequestId
    {
        get; init;
    }
}
