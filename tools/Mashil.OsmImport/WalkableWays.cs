using OsmSharp.Tags;

namespace Mashil.OsmImport;

/// <summary>OSM 길 하나를 걸을 수 있는지, 걸을 수 있으면 비용 가중치와 계단 여부.</summary>
public readonly record struct WayClass(bool Walkable, float CostFactor, bool Steps, string Highway)
{
    public static readonly WayClass No = new(false, 0, false, "");
}

/// <summary>
/// 보행 가능 판정 (https://wiki.openstreetmap.org/wiki/Key:highway, Key:foot, Key:access).
/// 보행로는 1, 큰 차도일수록 가중치를 올려 보행로·인도를 먼저 고르게 한다. 고속도로·자동차 전용 도로와 foot=no 는 뺀다.
/// </summary>
public static class WalkableWays
{
    public const float StepsFactor = 1.6f;

    private static readonly HashSet<string> FootYes = ["yes", "designated", "permissive", "official"];

    public static WayClass Classify(TagsCollectionBase? tags)
    {
        if (tags is null || !tags.TryGetValue("highway", out var highway) || string.IsNullOrEmpty(highway)) return WayClass.No;
        tags.TryGetValue("foot", out var foot);
        tags.TryGetValue("access", out var access);
        tags.TryGetValue("area", out var area);

        if (foot is "no" or "private" or "use_sidepath") return WayClass.No;
        if (access is "no" or "private" && !FootYes.Contains(foot ?? "")) return WayClass.No;
        if (area == "yes") return WayClass.No; // 광장 다각형은 테두리를 길로 오인하므로 뺀다

        float? factor = highway switch
        {
            "footway" or "pedestrian" or "path" or "living_street" or "corridor" => 1.0f,
            // 계단은 걷는 시간을 1.6배로 잡으므로(WalkPathFinder.StepsTimeFactor) 비용도 같게: 시간이 더 짧은 길이면 계단을 피한다
            "steps" => StepsFactor,
            "cycleway" => FootYes.Contains(foot ?? "") ? 1.0f : 1.2f,
            "track" or "service" or "residential" or "unclassified" or "road" => 1.1f,
            "tertiary" or "tertiary_link" => 1.2f,
            "secondary" or "secondary_link" => 1.3f,
            "primary" or "primary_link" => 1.4f,
            "trunk" or "trunk_link" => FootYes.Contains(foot ?? "") ? 1.5f : null,
            _ => null, // motorway, construction, proposed, platform, raceway, bus_guideway …
        };
        if (factor is null) return WayClass.No;

        // 차도에 인도가 따로 그려져 있으면(sidewalk=separate) 그 인도로 가게 차도 한가운데는 더 비싸게
        if (tags.TryGetValue("sidewalk", out var sidewalk) && sidewalk == "separate" && factor > 1.0f) factor += 0.3f;

        return new WayClass(true, factor.Value, highway == "steps", highway);
    }
}
