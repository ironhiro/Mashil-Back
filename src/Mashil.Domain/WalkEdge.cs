using NetTopologySuite.Geometries;

namespace Mashil.Domain;

/// <summary>
/// 도보 도로망의 간선 하나 (OpenStreetMap 에서 걸을 수 있는 길을 교차점마다 끊은 것). 방향 없음.
/// <c>tools/Mashil.OsmImport</c> 가 통째로 갈아 넣고, 길 안내(<c>WalkRouter</c>)가 읽기만 한다.
/// 출처: © OpenStreetMap contributors (ODbL) — 경로를 보여 주는 화면에 표시한다.
/// </summary>
public sealed class WalkEdge
{
    public long Id { get; set; }

    /// <summary>한쪽 끝 OSM 노드 id.</summary>
    public long Source { get; set; }

    /// <summary>다른 쪽 끝 OSM 노드 id.</summary>
    public long Target { get; set; }

    /// <summary>Source → Target 순서의 선 (WGS84, SRID 4326).</summary>
    public LineString Geom { get; set; } = null!;

    /// <summary>실제 길이 (m, 하버사인 합).</summary>
    public double LengthM { get; set; }

    /// <summary>비용 가중치 (1 = 보행로, 큰 차도일수록 큼). 비용 = 길이 × 가중치.</summary>
    public float CostFactor { get; set; } = 1f;

    /// <summary>계단 (걷는 시간을 더 잡고, 나중에 "계단 피하기"에 쓴다).</summary>
    public bool Steps { get; set; }

    /// <summary>OSM highway 값 (footway, residential …). 진단용.</summary>
    public string Highway { get; set; } = "";
}
