using System;
using System.Collections.Generic;
using System.Numerics;
using BozjaAssistant.Data;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

// FFXIVClientStructs 里也有一个 ObjectKind，与 Dalamud 的同名枚举冲突。
// 这里只处理 Dalamud 的对象表，所以显式指向 Dalamud 的枚举。
using ObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BozjaAssistant.Automation;

/// <summary>
/// 宝箱 / 胡萝卜采集模块。
///
/// 两类目标都**随机刷新**，预计算数据给的是「可能出现的候选点」而不是固定位置，所以本模块
/// 不靠预计算数据直接寻路，而是分两步：
///   1. 先在对象表里找**真实存在**的宝箱 / 胡萝卜对象，找到就直接前往（这是可靠路径）；
///   2. 对象表里没有时，才退化为按候选点顺序巡逻（预计算数据在这里派上用场）。
///
/// 第 1 步是主要路径。只靠预计算点巡逻会把随机刷新当成必然刷新，白跑很多趟。
/// </summary>
public sealed class HuntModule
{
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    /// <summary>巡逻进度：已经走过去看过（没有目标）的候选点，避免原地打转。</summary>
    private readonly HashSet<string> visitedNodes = new(StringComparer.Ordinal);

    private DateTime lastResetUtc = DateTime.MinValue;

    public HuntModule(IObjectTable objectTable, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.log = log;
    }

    public sealed record HuntTarget(
        string Name,
        Vector3 Position,
        float Distance,
        HuntKind Kind,
        /// <summary>来自真实对象表（true）还是候选点巡逻（false）。</summary>
        bool IsLiveObject,
        string NodeId);

    public enum HuntKind
    {
        None = 0,
        Treasure,
        Carrot,
    }

    /// <summary>候选点巡逻已走过的数量 / 总数量，用于界面显示进度。</summary>
    public int VisitedCount => this.visitedNodes.Count;

    public void ResetPatrol()
    {
        this.visitedNodes.Clear();
        this.lastResetUtc = DateTime.UtcNow;
        this.log.Debug("[Hunt] 巡逻进度已重置");
    }

    /// <summary>离开地图时调用：换了一张图，之前的进度没有意义。</summary>
    public void OnZoneChanged()
    {
        this.ResetPatrol();
    }

    /// <summary>
    /// 找当前可前往的采集目标。优先返回对象表里真实存在的目标，其次才是候选点。
    /// 返回 null 表示现在没有值得去的地方。
    /// </summary>
    public HuntTarget? FindTarget(Vector3 playerPosition, Configuration config)
    {
        var store = HuntDataStore.Instance;

        HuntTarget? live = null;
        if (config.HuntPreferLiveObjects)
        {
            live = this.FindLiveTreasure(playerPosition, config) ?? this.FindLiveCarrot(playerPosition, config);
            if (live is not null)
                return live;
        }

        // 对象表里没有。只有允许巡逻时才按候选点走——否则一直跑空路。
        if (!config.HuntPatrolCandidates)
            return null;

        return this.FindPatrolPoint(playerPosition, config, store);
    }

    /// <summary>
    /// 扫描对象表找宝箱。
    /// 新月岛的宝箱是 EventObj，名字里含「宝箱」；用名称匹配是因为国服没有稳定的 ObjectKind 可用，
    /// 而宝箱的 EventId / BaseId 每张图不同，硬编码会失效。
    /// </summary>
    private HuntTarget? FindLiveTreasure(Vector3 playerPosition, Configuration config)
    {
        HuntTarget? best = null;

        foreach (var obj in this.objectTable)
        {
            if (obj is null || obj.ObjectKind != ObjectKind.EventObj)
                continue;

            if (!LooksLikeTreasure(obj.Name.TextValue))
                continue;

            var distance = Vector3.Distance(obj.Position, playerPosition);
            if (distance > config.HuntMaxDistance)
                continue;

            if (best is null || distance < best.Distance)
                best = new HuntTarget(obj.Name.TextValue, obj.Position, distance, HuntKind.Treasure, true, string.Empty);
        }

        return best;
    }

    /// <summary>
    /// 扫描对象表找死·财富胡萝卜。
    /// EventObj 与 EventNpc 都可能是它，所以两种都看。
    /// </summary>
    private HuntTarget? FindLiveCarrot(Vector3 playerPosition, Configuration config)
    {
        HuntTarget? best = null;

        foreach (var obj in this.objectTable)
        {
            if (obj is null)
                continue;

            if (obj.ObjectKind is not (ObjectKind.EventObj or ObjectKind.EventNpc))
                continue;

            if (!LooksLikeCarrot(obj.Name.TextValue))
                continue;

            var distance = Vector3.Distance(obj.Position, playerPosition);
            if (distance > config.HuntMaxDistance)
                continue;

            if (best is null || distance < best.Distance)
                best = new HuntTarget(obj.Name.TextValue, obj.Position, distance, HuntKind.Carrot, true, string.Empty);
        }

        return best;
    }

    /// <summary>
    /// 按候选点巡逻：取距离玩家最近、且本趟还没去过的候选点。
    /// 全部去过之后清空重来——说明这轮刷新的东西已经没了，等下一轮。
    /// </summary>
    private HuntTarget? FindPatrolPoint(Vector3 playerPosition, Configuration config, HuntDataStore store)
    {
        if (!store.IsLoaded)
            return null;

        var nodes = new List<HuntNode>();
        if (config.HuntPatrolTreasure)
            nodes.AddRange(store.TreasureNodes);
        if (config.HuntPatrolCarrot)
            nodes.AddRange(store.CarrotNodes);

        if (nodes.Count == 0)
            return null;

        HuntNode? pick = null;
        var pickDistance = float.MaxValue;

        foreach (var node in nodes)
        {
            if (this.visitedNodes.Contains(node.Id))
                continue;

            var d = Vector3.Distance(node.Position, playerPosition);
            if (d > config.HuntMaxDistance)
                continue;

            if (d < pickDistance)
            {
                pickDistance = d;
                pick = node;
            }
        }

        if (pick is null)
        {
            // 范围内的都走过了。若整体走过一半以上，重置一轮，否则说明附近确实没有点。
            if (this.visitedNodes.Count >= nodes.Count / 2)
            {
                this.ResetPatrol();
                this.log.Debug("[Hunt] 候选点已走完一轮，重置巡逻");
            }

            return null;
        }

        return new HuntTarget($"候选点 {pick.Id}", pick.Position, pickDistance, HuntKind.Treasure, false, pick.Id);
    }

    /// <summary>
    /// 到达候选点后调用，把它标记为已巡查。
    /// 只对巡逻点生效；真实对象不需要记录，因为它消失本身就说明被取走了。
    /// </summary>
    public void MarkArrived(HuntTarget target)
    {
        if (target.IsLiveObject || string.IsNullOrEmpty(target.NodeId))
            return;

        this.visitedNodes.Add(target.NodeId);
    }

    /// <summary>距上次重置超过一定时间就清一次，避免跨版本残留。(目前用于每日刷新)</summary>
    public void MaybeResetDaily()
    {
        if ((DateTime.UtcNow - this.lastResetUtc).TotalHours >= 24)
            this.ResetPatrol();
    }

    // ---- 名称识别 ----
    // 国服 / 国际服 / 日服都能认出来。名称匹配是这里唯一可靠的手段：
    // 宝箱和胡萝卜的 BaseId 并非全图固定，也没法靠 EventId 预先枚举。

    private static bool LooksLikeTreasure(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("宝箱", StringComparison.Ordinal)
               || name.Contains("寶箱", StringComparison.Ordinal)
               || name.Contains("Treasure", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Coffer", StringComparison.OrdinalIgnoreCase)
               || name.Contains("チェスト", StringComparison.Ordinal);
    }

    private static bool LooksLikeCarrot(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains("胡萝卜", StringComparison.Ordinal)
               || name.Contains("胡蘿蔔", StringComparison.Ordinal)
               || name.Contains("Carrot", StringComparison.OrdinalIgnoreCase)
               || name.Contains("キャロット", StringComparison.Ordinal);
    }
}
