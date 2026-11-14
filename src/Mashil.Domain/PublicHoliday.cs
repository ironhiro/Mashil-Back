namespace Mashil.Domain;

/// <summary>
/// 공휴일 (개방 상태 판정용, MAP-05). 음력 공휴일·대체공휴일이 해마다 달라 코드에 고정하지 않고 테이블로 관리한다.
/// 비어 있으면 공휴일 규칙("공휴일 휴관")은 적용되지 않고 요일 규칙만 쓴다 (Q4).
/// </summary>
public sealed class PublicHoliday
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
}
