using System.Security.Cryptography;
using System.Text;

namespace Mashil.Import;

/// <summary>
/// 내려받은(또는 업로드된) 원본 파일. 전국파일은 약 16 MB 라 메모리에 통째로 올린다.
/// 인코딩은 내용으로 판별한다 — LOCALDATA 는 응답 헤더에 UTF-8 이라고 선언하지만 실제 바이트는 CP949 (G11).
/// </summary>
public sealed class SourceFile
{
    static SourceFile() => System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string? FileName { get; }
    public byte[] Bytes { get; }
    public string Sha256 { get; }
    public DetectedEncoding Encoding { get; }

    public SourceFile(byte[] bytes, string? fileName = null)
    {
        Bytes = bytes;
        FileName = fileName;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Encoding = DetectEncoding(bytes);
    }

    public static async Task<SourceFile> ReadAsync(string path, CancellationToken ct = default) =>
        new(await File.ReadAllBytesAsync(path, ct), Path.GetFileName(path));

    /// <summary>BOM 이 있으면 UTF-8, 엄격한 UTF-8 디코딩이 성공하면 UTF-8, 아니면 CP949.</summary>
    public static DetectedEncoding DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return DetectedEncoding.Utf8Bom;
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return DetectedEncoding.Utf8;
        }
        catch (DecoderFallbackException)
        {
            return DetectedEncoding.Cp949;
        }
    }

    /// <summary>판별된 인코딩으로 디코딩한 텍스트 리더 (BOM 은 건너뜀).</summary>
    public TextReader OpenText()
    {
        var (encoding, offset) = Encoding switch
        {
            DetectedEncoding.Utf8Bom => (System.Text.Encoding.UTF8, 3),
            DetectedEncoding.Utf8 => (System.Text.Encoding.UTF8, 0),
            _ => (System.Text.Encoding.GetEncoding(949), 0),
        };
        return new StreamReader(new MemoryStream(Bytes, offset, Bytes.Length - offset, writable: false), encoding, detectEncodingFromByteOrderMarks: false);
    }
}

public enum DetectedEncoding
{
    Utf8,
    Utf8Bom,
    Cp949,
}

public static class DetectedEncodingExtensions
{
    public static string ToLabel(this DetectedEncoding e) => e switch
    {
        DetectedEncoding.Utf8 => "UTF-8",
        DetectedEncoding.Utf8Bom => "UTF-8-BOM",
        _ => "CP949",
    };
}
