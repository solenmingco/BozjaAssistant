using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;

// FFXIVClientStructs 里也有一个 ObjectKind，与 Dalamud 的同名枚举冲突。
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BozjaAssistant.Automation;

/// <summary>
/// 两歧塔（Forked Tower）陷阱标记。
///
/// 陷阱是地图上的隐形危险物，踩上去会掉血或中状态。bozjalone 靠自有服务器收集玩家上报的
/// 陷阱坐标；我们没有那份数据，所以改成**运行时扫描**：找对象表里名称像陷阱的对象，
/// 把它们的位置画在原地形上。扫不到就没有标记——但这样至少不会显示过期的坑。
///
/// 玩家的「陷阱探测」(Trap Detection, 41648) 也会把陷阱变成可见对象，所以兼职盗贼时
/// 这个模块的命中率会明显变高。
/// </summary>
public sealed class ForkedTowerModule
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private readonly List<TrapMarker> markers = new();
    private DateTime nextScanUtc = DateTime.MinValue;

    /// <summary>扫描间隔。陷阱位置不会变，不需要逐帧扫。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    public ForkedTowerModule(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public sealed record TrapMarker(string Name, Vector3 Position, float Radius, DateTime DetectedAtUtc);

    public IReadOnlyList<TrapMarker> Markers => this.markers;

    /// <summary>当前所在区域是否值得扫描。两歧塔在博兹雅/新月岛都可能有。</summary>
    public static bool IsForkedTowerObject(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("陷阱", StringComparison.Ordinal)
               || name.Contains("Trap", StringComparison.OrdinalIgnoreCase)
               || name.Contains("トラップ", StringComparison.Ordinal);
    }

    public void Reset()
    {
        this.markers.Clear();
        this.nextScanUtc = DateTime.MinValue;
    }

    public void Tick(Vector3 playerPosition, Configuration config)
    {
        if (!config.ForkedTowerEnabled)
            return;

        var now = DateTime.UtcNow;
        if (now < this.nextScanUtc)
            return;

        this.nextScanUtc = now + ScanInterval;

        try
        {
            var found = new List<TrapMarker>();

            foreach (var obj in this.objectTable)
            {
                if (obj is null)
                    continue;

                // 陷阱通常是 EventObj；玩家的陷阱探测会把它显式暴露出来。
                if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.EventNpc))
                    continue;

                if (!IsForkedTowerObject(obj.Name.TextValue))
                    continue;

                var distance = Vector3.Distance(obj.Position, playerPosition);
                if (distance > config.ForkedTowerMaxDistance)
                    continue;

                found.Add(new TrapMarker(obj.Name.TextValue, obj.Position, 3.0f, now));
            }

            this.markers.Clear();
            this.markers.AddRange(found);

            if (found.Count > 0 && config.ForkedTowerAlert)
                this.log.Info($"[ForkedTower] 检测到 {found.Count} 个陷阱");
        }
        catch (Exception ex)
        {
            this.log.Debug($"[ForkedTower] 扫描失败：{ex.Message}");
        }
    }

    /// <summary>离玩家最近的陷阱距离。没有时返回 float.MaxValue。</summary>
    public float NearestDistance(Vector3 playerPosition)
    {
        var best = float.MaxValue;
        foreach (var m in this.markers)
        {
            var d = Vector3.Distance(m.Position, playerPosition);
            if (d < best)
                best = d;
        }

        return best;
    }
}
