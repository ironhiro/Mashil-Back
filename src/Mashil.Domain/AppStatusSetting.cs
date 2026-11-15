namespace Mashil.Domain;

/// <summary>
/// 관리자 화면에서 켜고 끄는 점검 모드 (ADM-06, OPS-05). 행은 1개(<see cref="SingletonId"/>)뿐이다.
/// 설정(App:Status)은 DB 에 행이 없거나 DB 에 닿지 못할 때의 폴백이고, 설정에서 켠 플래그는 DB 값과 상관없이 켜진다 (비상 스위치).
/// </summary>
public sealed class AppStatusSetting
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>전체 점검. 웹·앱이 점검 화면을 보여 주고, 쓰기는 503.</summary>
    public bool Maintenance { get; set; }

    /// <summary>읽기 전용 점검. 찾기는 되고 평가·댓글·제보 같은 쓰기만 503.</summary>
    public bool ReadOnly { get; set; }

    public string? Message { get; set; }

    /// <summary>다시 여는 예정 시각 (UTC).</summary>
    public DateTimeOffset? Until { get; set; }

    public Guid? UpdatedById { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
