using System.Text;

namespace Mashil.Import.Tests;

public class SourceFileTests
{
    [Fact]
    public void Cp949_bytes_are_detected_as_cp949_even_without_hint()
    {
        var file = TestCsv.File(TestCsv.Header, TestCsv.Row1);
        Assert.Equal(DetectedEncoding.Cp949, file.Encoding);

        using var reader = file.OpenText();
        Assert.StartsWith("개방자치단체코드,관리번호", reader.ReadLine());
    }

    [Fact]
    public void Utf8_without_bom_is_detected()
    {
        var file = new SourceFile(Encoding.UTF8.GetBytes("관리번호,화장실명\n1,가\n"));
        Assert.Equal(DetectedEncoding.Utf8, file.Encoding);
    }

    [Fact]
    public void Utf8_bom_is_detected_and_skipped()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("관리번호\n")).ToArray();
        var file = new SourceFile(bytes);
        Assert.Equal(DetectedEncoding.Utf8Bom, file.Encoding);

        using var reader = file.OpenText();
        Assert.Equal("관리번호", reader.ReadLine());
    }

    [Fact]
    public void Sha256_is_stable_hex()
    {
        var a = TestCsv.File(TestCsv.Header, TestCsv.Row1);
        var b = TestCsv.File(TestCsv.Header, TestCsv.Row1);
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(64, a.Sha256.Length);
    }
}
