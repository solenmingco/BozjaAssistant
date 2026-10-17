using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace BozjaAssistant.Automation;

/// <summary>
/// 刷怪（mob farming）模块。
///
/// 思路是「拉一批再打」而不是「见一个打一个」：在新月岛单刷时逐个追怪效率很低，
/// 把附近的怪聚成一堆再 AOE 才划算。状态机：
///
///   Searching ──找到足够多怪──▶ Pulling（跑到怪群中心附近）──▶ Fighting ──怪清完──▶ Searching
///
/// 拉怪阶段不主动攻击，只把怪引到一起；真正的输出交给外部循环插件（RSR / WrathCombo），
/// 与战斗模块保持一致的分工。
/// </summary>
public sealed class MobFarmerModule
{
    private readonly IObjectTable objectTable;
    private readonly NavmeshIpc navmesh;
    private readonly IPluginLog log;

    private DateTime lastPullUtc = DateTime.MinValue;
    private Vector3 lastPullAnchor;

    public MobFarmerModule(IObjectTable objectTable, NavmeshIpc navmesh, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.navmesh = navmesh;
        this.log = log;
    }

    public enum FarmState
    {
        Idle = 0,
        Searching,
        Pulling,
        Fighting,
        Resting,
    }

    public FarmState State { get; private set; } = FarmState.Idle;

    public string StateText => this.State switch
    {
        FarmState.Idle => "闲置",
        FarmState.Searching => "寻找怪物",
        FarmState.Pulling => "拉怪聚怪",
        FarmState.Fighting => "战斗中",
        FarmState.Resting => "休整",
        _ => "未知",
    };

    private const int MaxPullCount = 8;

    /// <summary>一屏内最近的若干只敌对目标。</summary>
    public List<IGameObject> ScanHostiles(Vector3 playerPosition, float maxDistance, float maxLevelDelta, byte playerLevel)
    {
        var result = new List<(IGameObject Obj, float Distance)>();

        foreach (var obj in this.objectTable)
        {
            // 模式匹配必须拆开写：C# 不允许在 'is not ... or ...' 里声明变量，
            // 而且我们需要的是 IBattleNpc 才能读到 Level / IsDead / IsTargetable。
            if (obj is not IBattleNpc npc)
                continue;

            if (npc.ObjectKind != ObjectKind.BattleNpc)
                continue;

            if (npc.IsDead || !npc.IsTargetable)
                continue;

            var distance = Vector3.Distance(obj.Position, playerPosition);
            if (distance > maxDistance)
                continue;

            // 等级差过滤：拉高等级怪会直接躺，对刷效率是负的。
            if (maxLevelDelta > 0 && npc.Level > 0)
            {
                if (playerLevel > 0 && npc.Level > playerLevel + maxLevelDelta)
                    continue;
            }

            result.Add((obj, distance));
        }

        return result
            .OrderBy(t => t.Distance)
            .Take(MaxPullCount)
            .Select(t => t.Obj)
            .ToList();
    }

    /// <summary>怪群中心，作为「拉到哪里」的锚点。</summary>
    public static Vector3 Centroid(IReadOnlyList<IGameObject> objects)
    {
        if (objects.Count == 0)
            return Vector3.Zero;

        var sum = Vector3.Zero;
        foreach (var o in objects)
            sum += o.Position;

        return sum / objects.Count;
    }

    /// <summary>
    /// 执行一次刷怪 tick。返回 true 表示本 tick 已经接管了移动，
    /// 调用方（AutomationController）应当跳过 FATE / CE 的寻路。
    /// </summary>
    public bool Tick(Vector3 playerPosition, byte playerLevel, Configuration config)
    {
        if (!config.MobFarmerEnabled)
        {
            this.State = FarmState.Idle;
            return false;
        }

        var hostiles = this.ScanHostiles(playerPosition, config.MobFarmerRange, config.MobFarmerMaxLevelDelta, playerLevel);

        if (hostiles.Count == 0)
        {
            if (this.State == FarmState.Fighting || this.State == FarmState.Pulling)
                this.log.Debug("[Mob] 怪已清空，回到搜索");

            this.State = FarmState.Searching;
            this.navmesh.Stop();
            return false;
        }

        // 数量够多就直接开拉；否则也在等，因为几只散怪的收益不如等它们聚过来。
        if (hostiles.Count < config.MobFarmerMinCount && this.State == FarmState.Searching)
        {
            this.State = FarmState.Searching;
            return false;
        }

        var center = Centroid(hostiles);
        var nearest = Vector3.Distance(hostiles[0].Position, playerPosition);

        // 拉怪：跑到怪群中心附近，让怪自己聚过来。
        this.State = this.navmesh.IsMoving ? FarmState.Pulling : FarmState.Fighting;

        if (nearest > config.MobFarmerEngageDistance)
        {
            this.State = FarmState.Pulling;

            var now = DateTime.UtcNow;
            var movedEnough = Vector3.Distance(this.lastPullAnchor, center) > 5f;
            var cooledDown = (now - this.lastPullUtc).TotalSeconds >= 1.0;

            if (movedEnough || cooledDown)
            {
                this.lastPullUtc = now;
                this.lastPullAnchor = center;
                this.navmesh.MoveCloseTo(center, 5f, config.MobFarmerFly);
            }

            return true;
        }

        // 已经在怪堆里：停手，交给外部循环插件输出。
        if (this.navmesh.IsMoving)
            this.navmesh.Stop();

        this.State = FarmState.Fighting;
        return true;
    }

    public void Reset()
    {
        this.State = FarmState.Idle;
        this.lastPullUtc = DateTime.MinValue;
    }
}
