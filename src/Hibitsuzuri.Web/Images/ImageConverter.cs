using System.Buffers.Binary;
using System.Text.Json;
using SkiaSharp;

namespace Hibitsuzuri.Web.Images;

/// <summary>ネイティブ画像処理を、DBを開かない子プロセスで実行する</summary>
public static class ImageConverter
{
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>検証・変換に失敗した入力は出力として採用させない</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 4)
        {
            return 2;
        }

        try
        {
            var result = Convert(args[1], args[2], args[3] != "webp", args[3] == "stored");
            Console.Write(JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OutOfMemoryException or IOException)
        {
            Console.Error.Write("画像を読み込めません。形式・破損・大きさを確認してください。");
            return 1;
        }
    }

    /// <summary>寸法・実体を検証し、静止画だけを必要に応じてWebP化する</summary>
    public static ImageResult Convert(string input, string output, bool original, bool stored = false)
    {
        // 保存時の可逆変換で元の20MBを超えた画像も、画素数の制約を維持して検証する。
        if (new FileInfo(input).Length < 1 || new FileInfo(input).Length > (stored ? 160000000 : MaxBytes))
        {
            throw new InvalidDataException();
        }

        var bytes = File.ReadAllBytes(input);
        var animatedContainer = ValidateContainer(bytes);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException();
        var media = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            SKEncodedImageFormat.Gif => "image/gif",
            _ => throw new InvalidDataException()
        };
        var info = codec.Info;
        // 圧縮後20MBの画像でも展開時にホストのメモリを使い切らないよう制限する。
        if (info.Width < 1 || info.Height < 1 || info.Width > 16384 || info.Height > 16384 || (long)info.Width * info.Height > 40000000)
        {
            throw new InvalidDataException();
        }

        using var srgb = SKColorSpace.CreateSrgb();
        using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            throw new InvalidDataException();
        }

        var swapped = (int)codec.EncodedOrigin >= 5;
        var width = swapped ? info.Height : info.Width;
        var height = swapped ? info.Width : info.Height;
        if (original || animatedContainer || codec.FrameCount > 1)
        {
            File.Copy(input, output);
            return new ImageResult(media, width, height);
        }

        var ratio = Math.Min(1, 2560d / Math.Max(width, height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * ratio));
        var targetHeight = Math.Max(1, (int)Math.Round(height * ratio));
        using var scaled = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, decoded.ColorType, decoded.AlphaType, srgb));
        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Transparent);
            var matrix = OriginMatrix(codec.EncodedOrigin, info.Width, info.Height);
            var sx = (float)targetWidth / width;
            var sy = (float)targetHeight / height;
            matrix.ScaleX *= sx;
            matrix.SkewX *= sx;
            matrix.TransX *= sx;
            matrix.SkewY *= sy;
            matrix.ScaleY *= sy;
            matrix.TransY *= sy;
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Flush();
        }

        // 新しいピクセル画像から符号化し、元写真の位置情報等をコピーしない。
        using var pixmap = scaled.PeekPixels();
        using var encoded = pixmap.Encode(new SKWebpEncoderOptions(ratio < 1 ? SKWebpEncoderCompression.Lossy : SKWebpEncoderCompression.Lossless, 80))
            ?? throw new InvalidDataException();
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoded.SaveTo(stream);
        stream.Flush(true);

        return new ImageResult("image/webp", targetWidth, targetHeight);
    }

    /// <summary>EXIFの回転と鏡像を画素へ反映する</summary>
    private static SKMatrix OriginMatrix(SKEncodedOrigin origin, int width, int height) => origin switch
    {
        SKEncodedOrigin.TopRight => Matrix(-1, 0, width, 0, 1, 0),
        SKEncodedOrigin.BottomRight => Matrix(-1, 0, width, 0, -1, height),
        SKEncodedOrigin.BottomLeft => Matrix(1, 0, 0, 0, -1, height),
        SKEncodedOrigin.LeftTop => Matrix(0, 1, 0, 1, 0, 0),
        SKEncodedOrigin.RightTop => Matrix(0, -1, height, 1, 0, 0),
        SKEncodedOrigin.RightBottom => Matrix(0, -1, height, -1, 0, width),
        SKEncodedOrigin.LeftBottom => Matrix(0, 1, 0, -1, 0, width),
        _ => SKMatrix.Identity
    };

    /// <summary>画像の向き補正に使うアフィン変換</summary>
    private static SKMatrix Matrix(float a, float b, float c, float d, float e, float f) => new()
    {
        ScaleX = a,
        SkewX = b,
        TransX = c,
        SkewY = d,
        ScaleY = e,
        TransY = f,
        Persp2 = 1
    };

    /// <summary>コンテナの欠損を拒否し、APNG・WebPのアニメーションを変換から除外する</summary>
    private static bool ValidateContainer(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            var offset = 8;
            var animated = false;
            while (offset <= bytes.Length - 12)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
                if (length > bytes.Length - offset - 12)
                {
                    throw new InvalidDataException();
                }

                var type = bytes.Slice(offset + 4, 4);
                animated |= type.SequenceEqual("acTL"u8);
                offset += checked((int)length + 12);
                if (type.SequenceEqual("IEND"u8))
                {
                    return offset == bytes.Length ? animated : throw new InvalidDataException();
                }
            }

            throw new InvalidDataException();
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
            {
                throw new InvalidDataException();
            }

            var offset = 12;
            var animated = false;
            while (offset <= bytes.Length - 8)
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
                if (length > bytes.Length - offset - 8)
                {
                    throw new InvalidDataException();
                }

                animated |= bytes.Slice(offset, 4).SequenceEqual("ANIM"u8);
                offset += checked(8 + (int)length + (int)(length & 1));
            }

            return offset == bytes.Length ? animated : throw new InvalidDataException();
        }

        if (bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)))
        {
            return bytes[^1] == 0x3b ? false : throw new InvalidDataException();
        }

        if (bytes.Length >= 4 && bytes[0] == 255 && bytes[1] == 216 && bytes[^2] == 255 && bytes[^1] == 217)
        {
            return false;
        }

        throw new InvalidDataException();
    }
}
