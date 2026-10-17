using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;

namespace BozjaAssistant.Data;

/// <summary>
/// 新月岛「宝箱 / 胡萝卜」候选点数据。
///
/// 这些坐标来自对 bozjalone 预计算路径数据的还原：它的 JSON 里只有「节点 → 节点」和
/// 「水晶 → 节点」的完整折线，节点自身的坐标并没有单独存一份。但折线的两个端点恰好就是
/// 起点与终点的世界坐标，所以把任意一条折线的起点/终点当作节点位置即可精确还原。
/// 还原后 25（胡萝卜）/ 68（宝箱）个节点全部齐全，且无一个节点缺位。
///
/// 嵌入的是还原出来的坐标本身，不是它的文件——路径仍然交给 vnavmesh 现算，
/// 因为我们无法保证它的折线在当前客户端版本下依然可达。
/// </summary>
public sealed class HuntDataStore
{
    private const string ResourceName = "BozjaAssistant.Data.HuntData.json";

    private static readonly Lazy<HuntDataStore> LazyInstance = new(Load, isThreadSafe: true);

    public static HuntDataStore Instance => LazyInstance.Value;

    /// <summary>胡萝卜（Fortune Carrot）的候选刷新点。</summary>
    public IReadOnlyList<HuntNode> CarrotNodes { get; }

    /// <summary>宝箱（青铜/白银/黄金宝箱）的候选刷新点。</summary>
    public IReadOnlyList<HuntNode> TreasureNodes { get; }

    /// <summary>新月岛的 5 个内部以太之光，带 PlaceNameId，可直接交给 Lifestream 传送。</summary>
    public IReadOnlyList<AethernetPoint> Aetherytes { get; }

    /// <summary>数据是否成功载入。载入失败时为 false，对应功能应自行禁用而不是抛异常。</summary>
    public bool IsLoaded { get; }

    public string? LoadError { get; }

    private HuntDataStore(IReadOnlyList<HuntNode> carrot, IReadOnlyList<HuntNode> treasure, IReadOnlyList<AethernetPoint> aetherytes, bool loaded, string? error)
    {
        this.CarrotNodes = carrot;
        this.TreasureNodes = treasure;
        this.Aetherytes = aetherytes;
        this.IsLoaded = loaded;
        this.LoadError = error;
    }

    private static HuntDataStore Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
                return new HuntDataStore(Array.Empty<HuntNode>(), Array.Empty<HuntNode>(), Array.Empty<AethernetPoint>(), false, $"嵌入资源 {ResourceName} 未找到");

            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var carrot = ReadNodes(root, "carrot");
            var treasure = ReadNodes(root, "treasure");
            var aetherytes = ReadAetherytes(root);

            var ok = carrot.Count > 0 && treasure.Count > 0 && aetherytes.Count > 0;
            return new HuntDataStore(carrot, treasure, aetherytes, ok, ok ? null : "数据为空");
        }
        catch (Exception ex)
        {
            return new HuntDataStore(Array.Empty<HuntNode>(), Array.Empty<HuntNode>(), Array.Empty<AethernetPoint>(), false, ex.Message);
        }
    }

    private static List<HuntNode> ReadNodes(JsonElement root, string section)
    {
        var result = new List<HuntNode>();
        if (!root.TryGetProperty(section, out var sec) || !sec.TryGetProperty("nodes", out var nodes))
            return result;

        foreach (var prop in nodes.EnumerateObject())
        {
            var p = prop.Value;
            if (!p.TryGetProperty("x", out var x) || !p.TryGetProperty("y", out var y) || !p.TryGetProperty("z", out var z))
                continue;

            result.Add(new HuntNode(prop.Name, new Vector3(x.GetSingle(), y.GetSingle(), z.GetSingle())));
        }

        return result;
    }

    private static List<AethernetPoint> ReadAetherytes(JsonElement root)
    {
        var result = new List<AethernetPoint>();
        if (!root.TryGetProperty("carrot", out var sec) || !sec.TryGetProperty("aethernet", out var ae))
            return result;

        foreach (var prop in ae.EnumerateObject())
        {
            var p = prop.Value;
            if (!p.TryGetProperty("placeNameId", out var id) || !p.TryGetProperty("x", out var x) ||
                !p.TryGetProperty("y", out var y) || !p.TryGetProperty("z", out var z))
                continue;

            result.Add(new AethernetPoint(prop.Name, id.GetUInt32(), new Vector3(x.GetSingle(), y.GetSingle(), z.GetSingle())));
        }

        return result;
    }

    /// <summary>离给定位置最近的候选点；数据为空时返回 null。</summary>
    public static HuntNode? Nearest(IReadOnlyList<HuntNode> nodes, Vector3 position)
    {
        HuntNode? best = null;
        var bestDistance = float.MaxValue;

        foreach (var node in nodes)
        {
            var d = Vector3.Distance(node.Position, position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = node;
            }
        }

        return best;
    }

    /// <summary>按世界 XZ 平面距离排序，返回前 count 个。用于「先探最近的几个点」。</summary>
    public static List<HuntNode> NearestMany(IReadOnlyList<HuntNode> nodes, Vector3 position, int count)
    {
        return nodes
            .Select(n => (Node: n, Distance: Vector3.Distance(n.Position, position)))
            .OrderBy(t => t.Distance)
            .Take(Math.Max(1, count))
            .Select(t => t.Node)
            .ToList();
    }

    public static string Describe()
    {
        var store = Instance;
        var culture = CultureInfo.InvariantCulture;
        return store.IsLoaded
            ? string.Format(culture, "宝箱节点 {0} 个，胡萝卜节点 {1} 个，水晶 {2} 个",
                store.TreasureNodes.Count, store.CarrotNodes.Count, store.Aetherytes.Count)
            : $"载入失败：{store.LoadError}";
    }
}

/// <summary>胡萝卜或宝箱的一个候选刷新点。Id 是原数据里的节点编号，用于日志对照。</summary>
public sealed record HuntNode(string Id, Vector3 Position);

/// <summary>新月岛内部以太之光。PlaceNameId 可由 Lifestream 的 AethernetTeleportByPlaceNameId 直接使用。</summary>
public sealed record AethernetPoint(string Name, uint PlaceNameId, Vector3 Position);
