using System.Diagnostics;
using System.Text.Json;

namespace Hibitsuzuri.Web.Images;

/// <summary>画像変換のクラッシュと長時間処理をWebホストから分離する</summary>
public sealed class ImageWorker
{
    /// <summary>子プロセスが正常終了した出力だけを採用する</summary>
    public async Task<ImageResult> ConvertAsync(string input, string output, bool original, CancellationToken cancellationToken, bool stored = false)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("image-convert");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(stored ? "stored" : original ? "original" : "webp");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("画像処理を開始できません。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var result = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0)
            {
                throw new InvalidDataException("画像を読み込めません。形式・破損・大きさを確認してください。");
            }

            return JsonSerializer.Deserialize<ImageResult>(await result) ?? throw new InvalidDataException();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidDataException("画像処理が時間内に完了しませんでした。画像を小さくして再度お試しください。");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
