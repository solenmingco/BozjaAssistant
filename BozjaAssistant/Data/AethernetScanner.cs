using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace BozjaAssistant.Data;

/// <summary>
/// Aethernet (以太之光碎片) discovery and cross-layer routing for the Occult
/// Crescent (新月岛).
///
/// Why this exists: 新月岛 is built in three vertically stacked layers, and each
/// layer is a separate walkable island — there is no ramp or stair between them.
/// The only way across is the internal Aethernet network: stand at a shard on
/// your current layer, open it, and warp to a shard on the target layer.
///
/// vnavmesh knows nothing about this. Handed a destination on another layer it
/// either fails to build a path or walks to the nearest point of the current
/// island and stops, which looks like the plugin "ignoring" the target. So the
/// routing decision has to be made here, before any path is requested.
///
/// How layers are told apart: the shards are ordinary <c>ObjectKind.Aetheryte</c>
/// actors, so their world positions are always available from the object table
/// with no data-table lookup or offset guesswork. Layers are separated by a
/// large vertical gap, so clustering shard heights gives the layer index. The
/// same clustering is then used to decide which layer an arbitrary world point
/// belongs to.
/// </summary>
public sealed class AethernetScanner
{
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    /// <summary>Lazily built display-name to PlaceName row id map.</summary>
    private Dictionary<string, uint>? placeNameIndex;

    /// <summary>Two shards further apart than this vertically are on different layers.</summary>
    private const float LayerHeightTolerance = 12f;

    /// <summary>How close to a shard counts as "in range" to use the network.</summary>
    public const float ShardInteractRange = 6f;

    /// <summary>Shards further than this from the player are not worth listing.</summary>
    private const float ShardScanRange = 40000f;

    public AethernetScanner(IObjectTable objectTable, IDataManager dataManager, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>One Aethernet shard in the current zone.</summary>
    /// <param name="Name">Display name, used for chat and for matching destinations.</param>
    /// <param name="Position">World position of the shard.</param>
    /// <param name="Layer">Layer index, ascending with height.</param>
    /// <param name="PlaceNameId">
    /// PlaceName sheet row id, when it can be resolved. Lifestream accepts this
    /// so the warp request does not depend on the client language.
    /// </param>
    public sealed record Shard(string Name, Vector3 Position, int Layer, uint PlaceNameId = 0)
    {
        public float DistanceTo(Vector3 point) => Vector3.Distance(this.Position, point);
    }

    /// <summary>Where the player is relative to the layer structure.</summary>
    public sealed record RoutingPlan(
        Shard? NearestShard,
        Shard? TargetLayerShard,
        int PlayerLayer,
        int TargetLayer,
        bool NeedsTransition)
    {
        /// <summary>True when a shard is close enough to open the network from.</summary>
        public bool InRangeOfShard => this.NearestShard is not null;
    }

    /// <summary>
    /// Enumerate every Aetheryte actor in the object table, then group them into
    /// layers by height.
    ///
    /// Returns an empty list outside the Occult Crescent or when the player has
    /// not attuned to anything yet, because a shard only becomes an actor once
    /// it is known to the client.
    /// </summary>
    public List<Shard> Scan(Vector3 playerPosition)
    {
        var raw = new List<(string Name, Vector3 Position, uint PlaceNameId)>();

        try
        {
            foreach (var obj in this.objectTable)
            {
                if (obj is null || obj.ObjectKind != ObjectKind.Aetheryte)
                {
                    continue;
                }

                // Cheap distance guard: the object table also holds far-away
                // actors during scene transitions.
                if (Vector3.Distance(obj.Position, playerPosition) > ShardScanRange)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(obj.Name.TextValue)
                    ? $"以太水晶@{obj.Position.Y:F0}"
                    : obj.Name.TextValue;

                raw.Add((name, obj.Position, this.ResolvePlaceNameId(name)));
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "读取以太水晶列表失败");
            return new List<Shard>();
        }

        if (raw.Count == 0)
        {
            return new List<Shard>();
        }

        // Sort by height, then split wherever the vertical gap is large enough to
        // be a different layer rather than a slope.
        raw.Sort((a, b) => a.Position.Y.CompareTo(b.Position.Y));

        var layerOfHeight = new List<float> { raw[0].Position.Y };
        foreach (var (_, position, _) in raw)
        {
            if (position.Y - layerOfHeight[^1] > LayerHeightTolerance)
            {
                layerOfHeight.Add(position.Y);
            }
        }

        var shards = new List<Shard>(raw.Count);
        foreach (var (name, position, placeNameId) in raw)
        {
            var layer = 0;
            for (var i = layerOfHeight.Count - 1; i >= 0; i--)
            {
                if (position.Y >= layerOfHeight[i] - LayerHeightTolerance)
                {
                    layer = i;
                    break;
                }
            }

            shards.Add(new Shard(name, position, layer, placeNameId));
        }

        return shards;
    }

    /// <summary>
    /// Find the PlaceName row whose text equals the shard's display name.
    ///
    /// Built once and cached: scanning the sheet on every tick would be wasteful,
    /// and the name-to-id mapping never changes within a session or a language.
    /// </summary>
    private uint ResolvePlaceNameId(string shardName)
    {
        this.placeNameIndex ??= this.BuildPlaceNameIndex();
        return this.placeNameIndex.TryGetValue(shardName, out var id) ? id : 0;
    }

    private Dictionary<string, uint> BuildPlaceNameIndex()
    {
        var index = new Dictionary<string, uint>(StringComparer.Ordinal);

        try
        {
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>();
            if (sheet is not null)
            {
                foreach (var row in sheet)
                {
                    var text = row.Name.ExtractText();
                    if (!string.IsNullOrWhiteSpace(text) && !index.ContainsKey(text))
                    {
                        index[text] = row.RowId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "构建 PlaceName 索引失败，将退回到按名称传送");
        }

        return index;
    }

    /// <summary>
    /// Decide how to reach <paramref name="targetPosition"/> given the shards in
    /// the zone.
    ///
    /// The plan is deliberately advisory: the caller still has to walk to the
    /// shard and open it. Returning a plan rather than acting keeps every game
    /// interaction in the controller, where the throttling and the state machine
    /// live.
    /// </summary>
    public RoutingPlan? PlanRoute(IReadOnlyList<Shard> shards, Vector3 playerPosition, Vector3 targetPosition)
    {
        if (shards.Count == 0)
        {
            return null;
        }

        var playerLayer = this.LayerOf(shards, playerPosition);
        var targetLayer = this.LayerOf(shards, targetPosition);

        // Nearest shard on the layer the player is standing on: that is the one we
        // can actually walk to.
        Shard? nearestShard = null;
        var nearestDistance = float.MaxValue;
        foreach (var shard in shards)
        {
            if (shard.Layer != playerLayer)
            {
                continue;
            }

            var distance = shard.DistanceTo(playerPosition);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestShard = shard;
            }
        }

        if (targetLayer == playerLayer)
        {
            return new RoutingPlan(nearestShard, null, playerLayer, targetLayer, NeedsTransition: false);
        }

        // Different layer: pick the shard on the target layer closest to the
        // destination, so the walk after the warp is as short as possible.
        Shard? targetShard = null;
        var bestTargetDistance = float.MaxValue;
        foreach (var shard in shards)
        {
            if (shard.Layer != targetLayer)
            {
                continue;
            }

            var distance = shard.DistanceTo(targetPosition);
            if (distance < bestTargetDistance)
            {
                bestTargetDistance = distance;
                targetShard = shard;
            }
        }

        return new RoutingPlan(
            nearestShard,
            targetShard,
            playerLayer,
            targetLayer,
            NeedsTransition: targetShard is not null);
    }

    /// <summary>
    /// Which layer a world point is on, using the shard heights as the reference.
    /// A point below every shard belongs to the lowest layer; the distance to the
    /// nearest reference decides otherwise.
    /// </summary>
    private int LayerOf(IReadOnlyList<Shard> shards, Vector3 point)
    {
        var best = 0;
        var bestDistance = float.MaxValue;

        foreach (var shard in shards)
        {
            // Horizontal distance dominates: a target on layer 0 and a shard on
            // layer 2 that happen to share an X/Z are different places.
            var distance = MathF.Abs(shard.Position.Y - point.Y);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = shard.Layer;
            }
        }

        return best;
    }

    /// <summary>
    /// Human-readable layer name. The Occult Crescent is described by players as
    /// 下层 / 中层 / 上层; the index is clamped because the north zone has four
    /// groups.
    /// </summary>
    public static string LayerName(int layer) => layer switch
    {
        0 => "下层",
        1 => "中层",
        2 => "上层",
        3 => "顶层",
        _ => $"第{layer + 1}层",
    };
}
