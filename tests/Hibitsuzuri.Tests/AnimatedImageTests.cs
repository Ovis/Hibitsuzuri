using System.Buffers.Binary;
using Hibitsuzuri.Web.Images;
using SkiaSharp;

namespace Hibitsuzuri.Tests;

/// <summary>PNG・WebPのアニメーションを静止画へ変換しないことを確認する</summary>
public sealed class AnimatedImageTests
{
    /// <summary>二フレームの実体をデコーダーへ渡し、原本のバイト列を比較する</summary>
    [TestCase("png")]
    [TestCase("webp")]
    public void AnimationIsPreserved(string format)
    {
        var folder = Path.Combine(Path.GetTempPath(), "hibitsuzuri-animation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var bytes = format == "png" ? Apng() : AnimatedWebp();
            var input = Path.Combine(folder, "input");
            var output = Path.Combine(folder, "output");
            File.WriteAllBytes(input, bytes);

            var result = ImageConverter.Convert(input, output, false);
            Assert.That(result.MediaType, Is.EqualTo("image/" + format));
            Assert.That(File.ReadAllBytes(output), Is.EqualTo(bytes));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    /// <summary>同じサイズで色の異なるフレームを作る</summary>
    private static byte[] Frame(SKColor color, SKEncodedImageFormat format)
    {
        using var bitmap = new SKBitmap(20, 10);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, 100);

        return encoded.ToArray();
    }

    /// <summary>PNGの正規チャンクをフレーム制御チャンクで束ねる</summary>
    private static byte[] Apng()
    {
        var first = Frame(SKColors.Red, SKEncodedImageFormat.Png);
        var second = Frame(SKColors.Blue, SKEncodedImageFormat.Png);
        using var output = new MemoryStream();
        output.Write(first.AsSpan(0, 8));
        output.Write(PngChunk("IHDR", PngData(first, "IHDR")));
        output.Write(PngChunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]));
        output.Write(PngChunk("fcTL", FrameControl(0)));
        output.Write(PngChunk("IDAT", PngData(first, "IDAT")));
        output.Write(PngChunk("fcTL", FrameControl(1)));
        output.Write(PngChunk("fdAT", [0, 0, 0, 2, .. PngData(second, "IDAT")]));
        output.Write(PngChunk("IEND", []));

        return output.ToArray();
    }

    /// <summary>APNGの連番・寸法・フレーム時間を指定する</summary>
    private static byte[] FrameControl(uint sequence)
    {
        var control = new byte[26];
        BinaryPrimitives.WriteUInt32BigEndian(control, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), 20);
        BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8), 10);
        control[21] = 1;
        control[23] = 10;

        return control;
    }

    /// <summary>既存PNGの指定チャンクのデータを連結する</summary>
    private static byte[] PngData(byte[] bytes, string kind)
    {
        using var output = new MemoryStream();
        for (var offset = 8; offset < bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset)));
            if (System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4) == kind)
            {
                output.Write(bytes, offset + 8, length);
            }

            offset += length + 12;
        }

        return output.ToArray();
    }

    /// <summary>CRCを含めた有効なPNGチャンクを作る</summary>
    private static byte[] PngChunk(string kind, byte[] data)
    {
        var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(kind).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        var crc = uint.MaxValue;
        foreach (var value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);

        return chunk;
    }

    /// <summary>WebPの二つの画像ビットストリームをアニメーションへまとめる</summary>
    private static byte[] AnimatedWebp()
    {
        using var payload = new MemoryStream();
        payload.Write("WEBP"u8);
        payload.Write(RiffChunk("VP8X", [2, 0, 0, 0, 19, 0, 0, 9, 0, 0]));
        payload.Write(RiffChunk("ANIM", new byte[6]));
        foreach (var color in new[] { SKColors.Red, SKColors.Blue })
        {
            var frame = Frame(color, SKEncodedImageFormat.Webp);
            byte[] header = [0, 0, 0, 0, 0, 0, 19, 0, 0, 9, 0, 0, 100, 0, 0, 2];
            payload.Write(RiffChunk("ANMF", [.. header, .. frame[12..]]));
        }

        var bytes = payload.ToArray();
        var result = new byte[bytes.Length + 8];
        "RIFF"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)bytes.Length);
        bytes.CopyTo(result, 8);

        return result;
    }

    /// <summary>偶数長に揃えたRIFFチャンクを作る</summary>
    private static byte[] RiffChunk(string kind, byte[] data)
    {
        var result = new byte[8 + data.Length + (data.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(kind).CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)data.Length);
        data.CopyTo(result, 8);

        return result;
    }
}
