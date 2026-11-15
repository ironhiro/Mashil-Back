using Mashil.Domain;

namespace Mashil.Import;

/// <summary>
/// DB 에 쓰기 전에 파일 전체를 한 번 읽어 판단한다 (DATA-20). 하나라도 어긋나면 반영하지 않고 기존 데이터를 유지.
/// 원본 사이트 장애로 비거나 일부만 담긴 파일이 오면 전국 화장실이 누락 처리되는 사고를 막는다.
/// </summary>
public static class ImportPreValidator
{
    public static PreValidation Validate(SourceFile file, ISourceAdapter adapter, ImportRun? lastSuccess, ImportPolicy policy, bool skipComparison = false)
    {
        int rows = 0, skipped = 0;
        try
        {
            using var reader = file.OpenText();
            foreach (var _ in adapter.Read(reader, (_, _) => skipped++)) rows++;
        }
        catch (SourceSchemaException ex)
        {
            return PreValidation.Reject(ex.Message, rows, skipped);
        }

        if (rows == 0)
            return PreValidation.Reject("데이터 행이 없습니다", rows, skipped);

        var skippedRatio = (double)skipped / (rows + skipped);
        if (skippedRatio > policy.MaxSkippedRatio)
            return PreValidation.Reject($"손상 행 비율 {skippedRatio:P1} > 허용 {policy.MaxSkippedRatio:P1}", rows, skipped);

        if (!skipComparison && lastSuccess is not null)
        {
            if (lastSuccess.FileSizeBytes > 0)
            {
                var sizeChange = Math.Abs(file.Bytes.LongLength - lastSuccess.FileSizeBytes) / (double)lastSuccess.FileSizeBytes;
                if (sizeChange > policy.MaxSizeChangeRatio)
                    return PreValidation.Reject($"파일 크기 변화 {sizeChange:P1} > 허용 {policy.MaxSizeChangeRatio:P0} (직전 {lastSuccess.FileSizeBytes:N0} → {file.Bytes.LongLength:N0} bytes)", rows, skipped);
            }

            var lastRows = lastSuccess.TotalRows - lastSuccess.SkippedRows;
            if (lastRows > 0)
            {
                var drop = (lastRows - rows) / (double)lastRows;
                if (drop > policy.MaxRowDropRatio)
                    return PreValidation.Reject($"행 수 감소 {drop:P1} > 허용 {policy.MaxRowDropRatio:P0} (직전 {lastRows:N0} → {rows:N0})", rows, skipped);
            }
        }

        return new PreValidation(true, null, rows, skipped);
    }
}

public sealed record PreValidation(bool Accepted, string? Reason, int Rows, int Skipped)
{
    public static PreValidation Reject(string reason, int rows, int skipped) => new(false, reason, rows, skipped);
}
