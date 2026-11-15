using System.Net;
using System.Text.Json;
using Hibitsuzuri.Web.Auth;
using Hibitsuzuri.Web.Images;
using Hibitsuzuri.Web.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Hibitsuzuri.Tests;

/// <summary>実際の画像処理・非公開配信・履歴と下書きの保持を検証する</summary>
public sealed class ImageTests
{
    /// <summary>EXIFの向きを寸法に反映し、写真メタデータを変換先へ持ち込まない</summary>
    [TestCase(1, 60, 40)]
    [TestCase(2, 60, 40)]
    [TestCase(3, 60, 40)]
    [TestCase(4, 60, 40)]
    [TestCase(5, 40, 60)]
    [TestCase(6, 40, 60)]
    [TestCase(7, 40, 60)]
    [TestCase(8, 40, 60)]
    public void OrientationIsAppliedWithoutCopyingExif(int origin, int width, int height)
    {
        var folder = Path.Combine(Path.GetTempPath(), "hibitsuzuri-exif-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var bitmap = SKBitmap.Decode(Png(60, 40));
            using var picture = SKImage.FromBitmap(bitmap);
            using var jpeg = picture.Encode(SKEncodedImageFormat.Jpeg, 95);
            var bytes = jpeg.ToArray();
            var exif = System.Convert.FromHexString("FFE1002245786966000049492A0008000000010012010300010000000100000000000000");
            exif[28] = (byte)origin;
            var input = Path.Combine(folder, "input");
            var output = Path.Combine(folder, "output");
            File.WriteAllBytes(input, [.. bytes[..2], .. exif, .. bytes[2..]]);

            var result = ImageConverter.Convert(input, output, false);
            Assert.That(result.Width, Is.EqualTo(width));
            Assert.That(result.Height, Is.EqualTo(height));
            Assert.That(System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output)), Does.Not.Contain("EXIF"));
            using var decoded = SKCodec.Create(output);
            Assert.That(decoded.EncodedOrigin, Is.EqualTo(SKEncodedOrigin.TopLeft));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>透明領域を白や黒で塗りつぶさず、可逆WebPに維持する</summary>
    [Test]
    public void TransparentPixelsSurviveLosslessConversion()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hibitsuzuri-alpha-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var bitmap = new SKBitmap(10, 10);
            bitmap.Erase(SKColors.Transparent);
            bitmap.SetPixel(5, 5, SKColors.Red);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            var input = Path.Combine(folder, "input");
            var output = Path.Combine(folder, "output");
            File.WriteAllBytes(input, encoded.ToArray());

            ImageConverter.Convert(input, output, false);
            using var result = SKBitmap.Decode(output);
            Assert.That(result.GetPixel(0, 0).Alpha, Is.Zero);
            Assert.That(result.GetPixel(5, 5), Is.EqualTo(SKColors.Red));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>小画像は可逆WebP、大画像は縮小し、原本設定はバイト列を維持する</summary>
    [Test]
    public void ConversionAppliesSizeAndOriginalPolicy()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hibitsuzuri-image-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var input = Path.Combine(folder, "input");
            var output = Path.Combine(folder, "output");
            var bytes = Png(60, 40);
            File.WriteAllBytes(input, bytes);
            var result = ImageConverter.Convert(input, output, false);
            using var bitmap = SKBitmap.Decode(output);
            Assert.That(result, Is.EqualTo(new ImageResult("image/webp", 60, 40)));
            Assert.That(bitmap.GetPixel(20, 20), Is.EqualTo(new SKColor(12, 34, 56, 255)));

            File.Delete(output);
            ImageConverter.Convert(input, output, true);
            Assert.That(File.ReadAllBytes(output), Is.EqualTo(bytes));

            File.Delete(output);
            File.WriteAllBytes(input, Png(3000, 1500));
            Assert.That(ImageConverter.Convert(input, output, false), Is.EqualTo(new ImageResult("image/webp", 2560, 1280)));

            File.Delete(output);
            File.WriteAllText(input, "<svg xmlns='http://www.w3.org/2000/svg'/>");
            Assert.Throws<InvalidDataException>(() => ImageConverter.Convert(input, output, true));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>二フレームGIFは変換設定でも原本を維持する</summary>
    [Test]
    public void AnimatedGifRemainsOriginal()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hibitsuzuri-gif-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // 1画素フレームを2回持つ、有効なGIF89a。
            var bytes = System.Convert.FromHexString("47494638396101000100800000000000FFFFFF21F904000A0000002C000000000100010000020244010021F904000A0000002C00000000010001000002024C01003B");
            var input = Path.Combine(folder, "input");
            var output = Path.Combine(folder, "output");
            File.WriteAllBytes(input, bytes);

            Assert.That(ImageConverter.Convert(input, output, false).MediaType, Is.EqualTo("image/gif"));
            Assert.That(File.ReadAllBytes(output), Is.EqualTo(bytes));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>変換子プロセスを経た画像は本人だけが読め、他人の参照を保存できない</summary>
    [Test]
    public async Task UploadAndDeliveryEnforceOwnershipAndSettings()
    {
        await using var app = new TestApplication();
        using var owner = app.CreateBrowser();
        using var other = app.CreateBrowser();
        await AuthClient.RegisterAsync(owner);
        await AuthClient.RegisterAsync(other, "other", false);
        var book = await DiaryClient.CreateBookAsync(owner);
        var otherBook = await DiaryClient.CreateBookAsync(other);
        var draft = Guid.NewGuid().ToString();
        var id = await UploadAsync(owner, book, draft);

        using var read = await owner.GetAsync($"/images/{id}");
        using var denied = await other.GetAsync($"/images/{id}");
        using var saveDenied = await DiaryClient.SaveAsync(other, otherBook, $"![秘密](attachment:{id})");
        Assert.That(read.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/webp"));
        Assert.That(read.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(denied.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(saveDenied.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await DiaryClient.SaveVersionAsync(owner, book, $"![写真](attachment:{id})");
        var html = await owner.GetStringAsync($"/diary/{book}/2020-05-10");
        Assert.That(html, Does.Contain($"src=\"/images/{id}\""));

        using var settings = await AuthClient.PostAsync(owner, "/settings/images", new()
        {
            ["keepOriginal"] = "true"
        });
        var original = await UploadAsync(owner, book, draft);
        using var originalRead = await owner.GetAsync($"/images/{original}");
        Assert.That(originalRead.Content.Headers.ContentType?.MediaType, Is.EqualTo("image/png"));
        Assert.That(await originalRead.Content.ReadAsByteArrayAsync(), Is.EqualTo(Png(60, 40)));

        using var invalid = await UploadResponseAsync(owner, book, draft, "<svg/>"u8.ToArray());
        Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    /// <summary>下書きを延長しても別候補を破棄せず、30日境界では画像だけが利用不可になる</summary>
    [Test]
    public async Task DraftLeaseExpiresAndCanBeRenewedBeforeBoundary()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var draft = Guid.NewGuid().ToString();
        var image = await UploadAsync(client, book, draft);
        app.Clock.Advance(TimeSpan.FromDays(29));
        using var login = await AuthClient.LoginAsync(client);
        using var renewal = await AuthClient.PostAsync(client, "/api/images/draft", new()
        {
            ["draftId"] = Guid.NewGuid().ToString(),
            ["content"] = $"![写真](attachment:{image})"
        });
        using var discard = await AuthClient.PostAsync(client, "/api/images/draft/discard", new()
        {
            ["draftId"] = draft,
            ["content"] = ""
        });
        app.Clock.Advance(TimeSpan.FromDays(29));
        using var loginAgain = await AuthClient.LoginAsync(client);
        using var stillHere = await client.GetAsync($"/images/{image}");
        Assert.That(stillHere.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        app.Clock.Advance(TimeSpan.FromDays(1));
        using var loginFinal = await AuthClient.LoginAsync(client);
        using var expired = await client.GetAsync($"/images/{image}");
        using var save = await DiaryClient.SaveAsync(client, book, $"本文は保持 ![写真](attachment:{image})");
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(save.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        await CleanupAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().DiaryImages.CountAsync(), Is.Zero);
        Assert.That(File.Exists(scope.ServiceProvider.GetRequiredService<ImageStore>().PathFor(image)), Is.False);
    }

    /// <summary>ゴミ箱と過去5版で保持し、最後の参照が落ちた画像と中断ファイルを回収する</summary>
    [Test]
    public async Task CleanupPreservesHistoryAndTrashUntilLastReferenceDisappears()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var draft = Guid.NewGuid().ToString();
        var image = await UploadAsync(client, book, draft);
        var version = await DiaryClient.SaveVersionAsync(client, book, $"![写真](attachment:{image})");
        using var discard = await AuthClient.PostAsync(client, "/api/images/draft/discard", new()
        {
            ["draftId"] = draft,
            ["content"] = ""
        });
        version = await DiaryClient.SaveVersionAsync(client, book, "画像を外す", version);
        await CleanupAsync(app);
        using var retained = await client.GetAsync($"/images/{image}");
        Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var trash = await AuthClient.PostAsync(client, $"/diary/{book}/2020-05-10/trash", new()
        {
            ["action"] = "delete",
            ["version"] = version
        });
        await CleanupAsync(app);
        using var retainedInTrash = await client.GetAsync($"/images/{image}");
        Assert.That(retainedInTrash.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        version = await db.DiaryEntries.Select(x => x.Version).SingleAsync();
        using var restore = await AuthClient.PostAsync(client, $"/diary/{book}/2020-05-10/trash", new()
        {
            ["action"] = "restore",
            ["version"] = version
        });
        version = await db.DiaryEntries.Select(x => x.Version).SingleAsync();
        for (var i = 0; i < 5; i++)
        {
            version = await DiaryClient.SaveVersionAsync(client, book, "新版" + i, version);
        }

        var orphan = Path.Combine(scope.ServiceProvider.GetRequiredService<DataDirectory>().ImagesPath, "work-interrupted.input");
        File.WriteAllText(orphan, "interrupted");
        await CleanupAsync(app);
        Assert.That(await db.DiaryImages.CountAsync(), Is.Zero);
        Assert.That(File.Exists(orphan), Is.False);
    }

    /// <summary>切り取り中の下書き更新と回収を挟んでも、貼り付けた画像を表示・保存できる</summary>
    [Test]
    public async Task CutAndPasteKeepsUnsavedImageAvailable()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var draft = Guid.NewGuid().ToString();
        var image = await UploadAsync(client, book, draft);

        using var cut = await AuthClient.PostAsync(client, "/api/images/draft", new()
        {
            ["draftId"] = draft,
            ["content"] = "画像の移動中"
        });
        Assert.That(cut.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await CleanupAsync(app);

        var text = $"先頭へ移動 ![写真](attachment:{image})\n本文";
        using var pasted = await AuthClient.PostAsync(client, "/api/images/draft", new()
        {
            ["draftId"] = draft,
            ["content"] = text
        });
        using var renewed = JsonDocument.Parse(await pasted.Content.ReadAsStringAsync());
        Assert.That(renewed.RootElement.GetProperty("missing").GetArrayLength(), Is.Zero);

        using var preview = await AuthClient.PostAsync(client, "/api/diary/preview", new()
        {
            ["content"] = text
        });
        using var rendered = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        Assert.That(rendered.RootElement.GetProperty("html").GetString(), Does.Contain($"/images/{image}").And.Not.Contain("画像が見つかりません"));
        using var saved = await DiaryClient.SaveAsync(client, book, text);
        Assert.That(saved.StatusCode, Is.EqualTo(HttpStatusCode.OK), await saved.Content.ReadAsStringAsync());
    }

    /// <summary>外した画像の期限は延長せず、未参照のまま30日が経過した画像を回収する</summary>
    [Test]
    public async Task RemovedReferenceKeepsOriginalExpiryWithoutRenewal()
    {
        await using var app = new TestApplication();
        using var client = app.CreateBrowser();
        await AuthClient.RegisterAsync(client);
        var book = await DiaryClient.CreateBookAsync(client);
        var draft = Guid.NewGuid().ToString();
        var image = await UploadAsync(client, book, draft);
        app.Clock.Advance(TimeSpan.FromDays(29));
        using var login = await AuthClient.LoginAsync(client);

        using var cut = await AuthClient.PostAsync(client, "/api/images/draft", new()
        {
            ["draftId"] = draft,
            ["content"] = ""
        });
        using var retained = await client.GetAsync($"/images/{image}");
        Assert.That(retained.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        app.Clock.Advance(TimeSpan.FromDays(1));
        using var loginAgain = await AuthClient.LoginAsync(client);
        using var expired = await client.GetAsync($"/images/{image}");
        Assert.That(expired.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        await CleanupAsync(app);

        await using var scope = app.Services.CreateAsyncScope();
        Assert.That(await scope.ServiceProvider.GetRequiredService<AppDbContext>().DiaryImages.CountAsync(), Is.Zero);
    }

    /// <summary>画素の比較ができる不透明なPNGを作る</summary>
    private static byte[] Png(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(12, 34, 56, 255));
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);

        return encoded.ToArray();
    }

    /// <summary>実際のCSRFトークン付きmultipartで画像を送る</summary>
    private static async Task<HttpResponseMessage> UploadResponseAsync(HttpClient client, string book, string draft, byte[] bytes)
    {
        var token = await client.GetStringAsync("/fixture/token");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent(draft), "draftId");
        form.Add(new ByteArrayContent(bytes), "file", "写真.png");

        return await client.PostAsync($"/api/images/{book}/2020-05-10", form);
    }

    /// <summary>取り込み成功を確認して画像IDを返す</summary>
    private static async Task<string> UploadAsync(HttpClient client, string book, string draft)
    {
        using var response = await UploadResponseAsync(client, book, draft, Png(60, 40));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return result.RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException();
    }

    /// <summary>実運用と同じゲートで参照確認と回収を直列化する</summary>
    private static async Task CleanupAsync(TestApplication app)
    {
        var gate = app.Services.GetRequiredService<AuthWriteGate>();
        await gate.Semaphore.WaitAsync();
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ImageStore>().CleanupAsync(CancellationToken.None);
        }
        finally
        {
            gate.Semaphore.Release();
        }
    }
}
