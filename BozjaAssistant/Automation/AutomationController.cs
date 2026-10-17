using System;
using System.Collections.Generic;
using System.Numerics;
using BozjaAssistant.Data;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BozjaAssistant.Automation;

/// <summary>
/// The automation state machine.
///
/// It runs on Dalamud's framework thread (the same thread that owns the game
/// state), so every read of a game struct and every IPC call happens on a safe
/// thread. The loop is intentionally simple: determine the zone, decide whether
/// this module should be acting at all, pick a target, and keep travelling until
/// it arrives or the situation changes.
///
/// Design notes:
///  - The controller never blocks. Each tick does a bounded amount of work and
///    returns; anything time-based (rescan interval, revive delay) is expressed as
///    a deadline compared against the clock, not a sleep.
///  - Every module is independently switchable and independently guarded, so a
///    failing optional plugin (vnavmesh missing, for example) degrades to "that
///    feature is off" rather than breaking the loop.
/// </summary>
public sealed class AutomationController : IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration config;
    private readonly ActivityLog activity;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private DateTime lastTick = DateTime.MinValue;
    private DateTime lastFateRescan = DateTime.MinValue;
    private DateTime lastCombatTick = DateTime.MinValue;
    private DateTime deathTime = DateTime.MinValue;

    private TargetSelector.Target currentTarget = TargetSelector.None;
    private HuntModule.HuntTarget? currentHuntTarget;
    private bool wasDead;
    private bool disposed;

    /// <summary>
    /// Set once we have warped for the current target. Stops us from warping
    /// repeatedly when the shard we landed on is still the nearest one.
    /// </summary>
    private bool warpedForCurrentTarget;

    /// <summary>Player position in world coordinates.</summary>
    private Vector3 PlayerPosition
    {
        get
        {
            var player = this.objectTable.LocalPlayer;
            return player?.Position ?? Vector3.Zero;
        }
    }

    public AutomationController(Plugin plugin)
    {
        this.plugin = plugin;
        this.config = plugin.Configuration;
        this.activity = plugin.Activity;
        this.clientState = Plugin.ClientState;
        this.objectTable = Plugin.ObjectTable;
        this.log = Plugin.Log;

        Plugin.Framework.Update += this.OnFrameworkUpdate;
    }

    /// <summary>Short human-readable state, shown in chat and the DTR bar.</summary>
    public string StatusText { get; private set; } = "空闲";

    /// <summary>Turn the loop on.</summary>
    public void Start()
    {
        if (this.config.EmergencyStop)
        {
            Plugin.Chat.Print("[Bozja] 紧急停止尚未解除，请先在界面中解除。");
            return;
        }

        this.config.AutomationEnabled = true;
        this.config.Save();
        this.activity.Success("已开始自动循环。");
        Plugin.Chat.Print("[Bozja] 已开始自动循环。");
    }

    /// <summary>Turn the loop off. Does not clear the emergency stop.</summary>
    public void Stop()
    {
        this.config.AutomationEnabled = false;
        this.config.Save();
        this.Halt();
        this.activity.Add("已停止自动循环。");
        Plugin.Chat.Print("[Bozja] 已停止自动循环。");
    }

    /// <summary>
    /// Stop everything immediately and latch. Deliberately requires a manual
    /// clear: an accidental restart after a panic is worse than an extra click.
    /// </summary>
    public void EmergencyStop()
    {
        this.config.EmergencyStop = true;
        this.config.AutomationEnabled = false;
        this.config.Save();
        this.Halt();
        this.activity.Error("紧急停止已触发。");
        Plugin.Chat.Print("[Bozja] 紧急停止已触发，需在界面中手动解除。");
    }

    /// <summary>Cancel all in-flight movement and detach from external AI plugins.</summary>
    private void Halt()
    {
        this.currentTarget = TargetSelector.None;
        this.plugin.Navmesh.Stop();
        this.plugin.Lifestream.Abort();
        this.plugin.CombatAi.Disable(this.config.ExternalAi);
        this.plugin.Mount.Reset();
        this.StatusText = "空闲";
        this.plugin.UpdateDtr("Bozja: 空闲");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (this.disposed)
        {
            return;
        }

        // The tick is throttle-driven rather than every frame: the game state does
        // not change fast enough to justify per-frame scanning, and this keeps the
        // cost invisible.
        var now = DateTime.UtcNow;
        if ((now - this.lastTick).TotalMilliseconds < 200)
        {
            return;
        }

        this.lastTick = now;

        try
        {
            this.Tick(now);
        }
        catch (Exception ex)
        {
            // A throwing framework callback would be removed by Dalamud, which
            // would silently disable the plugin. Swallow, log, keep the loop alive.
            this.log.Error(ex, "自动循环 tick 发生异常");
        }
    }

    private void Tick(DateTime now)
    {
        var player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            return;
        }

        this.HandleDeath(player.IsDead, now);

        if (!this.config.AutomationEnabled || this.config.EmergencyStop)
        {
            return;
        }

        var zone = CriticalEngagementScanner.DetectZone(Plugin.ClientState.TerritoryType);
        if (this.config.OnlyInSupportedZones && zone == CriticalEngagementScanner.ContentZone.None)
        {
            this.StatusText = "非特殊地图";
            this.plugin.UpdateDtr("Bozja: 非特殊地图");
            if (this.currentTarget.Kind != TargetSelector.TargetKind.None)
            {
                this.Halt();
            }

            return;
        }

        var playerPosition = this.PlayerPosition;

        // Drop alerts run regardless of travel state: they are passive.
        this.plugin.DropAlerts.Tick();
        this.plugin.Mount.Tick();

        // 采集 / 陷阱 / 统计都是被动或节流的，放在这里跑，不参与目标选择。
        this.plugin.Hunt.MaybeResetDaily();
        this.plugin.ForkedTower.Tick(playerPosition, this.config);
        this.SampleStats();

        // 幻影职业 Buff：不参与寻路决策，但可能临时切走职业，
        // 因此必须在刷怪与战斗之前处理，避免切职业时正在放技能。
        this.TickBuffs(now, player);

        if ((now - this.lastCombatTick).TotalSeconds >= 0.5)
        {
            this.lastCombatTick = now;
            this.plugin.Combat.Tick(playerPosition);
        }

        // 刷怪模块独立于 FATE / CE：它在自己拉怪时接管移动，
        // 返回 true 表示本 tick 不应再下发 FATE/CE 的寻路指令。
        var farming = this.plugin.MobFarmer.Tick(playerPosition, player.Level, this.config);
        if (farming)
        {
            this.StatusText = $"刷怪：{this.plugin.MobFarmer.StateText}";
            this.plugin.UpdateDtr($"Bozja: 刷怪 {this.plugin.MobFarmer.StateText}");
            return;
        }

        this.UpdateTravel(now, zone, playerPosition);
    }

    /// <summary>
    /// Handle the dead/alive transition. Revival is the one place where acting
    /// immediately is unambiguously correct, so it is not gated on target choice.
    /// </summary>
    private void HandleDeath(bool isDead, DateTime now)
    {
        if (isDead && !this.wasDead)
        {
            this.wasDead = true;
            this.deathTime = now;
            this.activity.Warning("角色已死亡。");
            this.plugin.Navmesh.Stop();
            this.currentTarget = TargetSelector.None;
        }
        else if (!isDead && this.wasDead)
        {
            this.wasDead = false;
            this.activity.Success("角色已复活。");
        }
    }

    /// <summary>
    /// The core travel loop: pick a target when we do not have one, keep walking,
    /// and re-evaluate periodically.
    /// </summary>
    private void UpdateTravel(DateTime now, CriticalEngagementScanner.ContentZone zone, Vector3 playerPosition)
    {
        var needsRescan = this.currentTarget.Kind == TargetSelector.TargetKind.None
                          || (now - this.lastFateRescan).TotalSeconds >= Math.Max(1, this.config.FateRescanSeconds);

        if (needsRescan)
        {
            this.lastFateRescan = now;
            this.Rescan(zone, playerPosition);
        }

        if (this.currentTarget.Kind == TargetSelector.TargetKind.None)
        {
            this.StatusText = "没有可执行的目标";
            this.plugin.UpdateDtr("Bozja: 待机");
            return;
        }

        this.ContinueTravel(playerPosition);
    }

    /// <summary>Re-evaluate the world and choose what to go after.</summary>
    private void Rescan(CriticalEngagementScanner.ContentZone zone, Vector3 playerPosition)
    {
        if (!this.plugin.Navmesh.IsAvailable)
        {
            this.StatusText = "vnavmesh 不可用";
            this.plugin.UpdateDtr("Bozja: 缺少 vnavmesh");
            return;
        }

        // Critical Engagements first: they are time-boxed, so missing the window
        // is unrecoverable, whereas a FATE can usually be picked up later.
        if (this.config.CeOverridesFate)
        {
            var ce = TargetSelector.SelectCe(this.plugin.Engagements.Scan(zone), playerPosition, this.config);
            if (ce is not null)
            {
                this.SetTarget(ce);
                return;
            }
        }

        var fate = TargetSelector.SelectFate(this.plugin.Fates.Scan(playerPosition, runningOnly: true), playerPosition, this.config);
        if (fate is not null)
        {
            this.SetTarget(fate);
            return;
        }

        // No CE qualified (or CE-over-FATE is off); try CE anyway before giving up.
        if (!this.config.CeOverridesFate)
        {
            var ce = TargetSelector.SelectCe(this.plugin.Engagements.Scan(zone), playerPosition, this.config);
            if (ce is not null)
            {
                this.SetTarget(ce);
                return;
            }
        }

        // 没有 FATE / CE 可打时，才去看宝箱与胡萝卜。
        // 放在最后是有意的：采集的收益远低于战斗内容，不该抢在前面。
        if (this.config.HuntEnabled)
        {
            var hunt = this.plugin.Hunt.FindTarget(playerPosition, this.config);
            if (hunt is not null)
            {
                // Kind 仍记为 Fate —— TargetKind 描述的是「走哪条路去」，
                // 采集点与 FATE 一样是「走过去、到了就停」，没有专属形态。
                // 名称前缀让它在本控制器里可以与其他 FATE 区分开。
                this.SetTarget(new TargetSelector.Target(
                    TargetSelector.TargetKind.Fate,
                    hunt.Name,
                    hunt.Position,
                    hunt.Distance,
                    $"{(hunt.IsLiveObject ? "发现" : "巡逻")}采集点（{hunt.Distance:F0} 米）"));
                this.currentHuntTarget = hunt;
                return;
            }
        }

        this.currentHuntTarget = null;
        this.currentTarget = TargetSelector.None;
    }

    /// <summary>
    /// 施放幻影职业增益。整套动作是「切职业 → 放技能 → 切回」，
    /// 分三个 tick 完成，因为职业切换要等游戏认了才放得出技能。
    /// </summary>
    private void TickBuffs(DateTime now, Dalamud.Game.ClientState.Objects.Types.IGameObject player)
    {
        if (!this.config.BuffEnabled || this.config.EmergencyStop)
        {
            this.plugin.Buff.Reset();
            return;
        }

        try
        {
            var currentJob = BuffModule.GetCurrentJob();
            if (currentJob == 0)
            {
                // 不在新月岛或没持有幻影职业，什么都不做。
                return;
            }

            // StatusList 在 IBattleChara 上，IGameObject 没有这个成员。
            if (player is not Dalamud.Game.ClientState.Objects.Types.IBattleChara battleChara)
                return;

            var statuses = new List<(uint StatusId, float RemainingSeconds)>();
            foreach (var s in battleChara.StatusList)
            {
                statuses.Add((s.StatusId, s.RemainingTime));
            }

            // 分三段推进：切职业 → 放技能 → 切回。每段跨 tick，
            // 因为职业切换后游戏要一拍才认。
            switch (this.plugin.Buff.Phase)
            {
                case BuffModule.ApplyStage.SwitchedToBuffJob:
                case BuffModule.ApplyStage.Casting:
                    if (this.plugin.Buff.IsReadyToCast)
                    {
                        // 技能由外部循环插件负责按，插件本身不施放技能；
                        // 这里只是等一个短窗口让循环插件在正确职业下发招，
                        // 随后就切回原职业，避免长时间占着增益职业。
                        this.plugin.Buff.FinishCast();
                    }

                    return;

                case BuffModule.ApplyStage.RestoringOriginal:
                    this.plugin.Buff.EndApply();
                    return;
            }

            var due = this.plugin.Buff.PickDueBuff(this.config, statuses, currentJob);
            if (due is null)
            {
                return;
            }

            if (this.plugin.Buff.BeginApply(due, currentJob))
            {
                this.activity.Add($"施放增益 {due.Name}（幻影职业 {due.SupportJobId}）");
            }
        }
        catch (Exception ex)
        {
            this.plugin.Buff.Reset();
            this.log.Debug(ex, "[Buff] 处理增益时出错");
        }
    }

    /// <summary>
    /// 记录收益采样，供每小时统计使用。
    ///
    /// 新月岛的真实产出口径是 OccultCrescentState 里的银币 / 金币 / 知识值，
    /// 而不是角色的经验条 —— Dalamud 15 的 IPlayerCharacter 根本没有暴露经验字段。
    /// 读内容状态既准确又和这片地的实际收益对得上。
    /// </summary>
    private unsafe void SampleStats()
    {
        if (!this.config.StatsEnabled || !this.plugin.Stats.ShouldSample())
        {
            return;
        }

        try
        {
            var state = PublicContentOccultCrescent.GetState();
            if (state is null)
            {
                return;
            }

            this.plugin.Stats.Record(
                exp: state->CurrentKnowledge,
                silver: state->Silver,
                gold: state->Gold,
                cluster: state->NeededJobExperience);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "[Stats] 采样失败");
        }
    }

    private void SetTarget(TargetSelector.Target target)
    {
        var changed = this.currentTarget.Kind != target.Kind
                      || !string.Equals(this.currentTarget.Name, target.Name, StringComparison.Ordinal);

        this.currentTarget = target;

        if (!changed)
        {
            return;
        }

        // A new target invalidates any previous warp bookkeeping.
        this.warpedForCurrentTarget = false;

        this.activity.Add($"前往 {target.Name}：{target.Reason}");
        this.log.Information("新目标 {Kind} {Name}，{Reason}", target.Kind, target.Name, target.Reason);

        // Start walking immediately rather than waiting for the next tick, so the
        // first step happens as soon as the decision is made.
        this.IssueMove(target);
    }

    /// <summary>
    /// Keep the character moving toward the current target.
    /// </summary>
    private void ContinueTravel(Vector3 playerPosition)
    {
        // Cross-layer handling comes first. The Occult Crescent layers are three
        // unconnected islands, so a plain path request for a target on another
        // layer either fails outright or silently walks to the edge of this
        // island and parks there. Warping has to be decided before pathing.
        if (this.config.AethernetRouting && this.TryHandleCrossLayer(playerPosition))
        {
            return;
        }

        var distance = Vector3.Distance(playerPosition, this.currentTarget.Position);

        // Close enough for FATE/CE participation. Both are areas with a radius, so
        // we aim for the marker rather than an exact point.
        if (distance <= ArrivalRange)
        {
            // Dismount on arrival: staying mounted prevents interacting with the
            // FATE objective and can keep us out of the engagement range.
            if (this.config.AutoMount)
            {
                this.plugin.Mount.Dismount();
            }

            this.StatusText = $"已到达 {this.currentTarget.Name}";
            this.plugin.UpdateDtr($"Bozja: {this.currentTarget.Name}");
            return;
        }

        this.StatusText = $"前往 {this.currentTarget.Name}（{distance:F0} 米）";
        this.plugin.UpdateDtr($"Bozja: {this.StatusText}");

        // Mount up for long trips. This runs before re-issuing the move order so
        // that the summon happens while vnavmesh is still walking; if the summon
        // interrupts pathing, the IsBusy check below re-issues it on a later tick.
        if (this.config.AutoMount)
        {
            this.plugin.Mount.TryMount(distance, this.config.AutoMountDistance);
        }

        // Re-issue the order if navigation has stopped without us arriving — this
        // happens when the path is invalidated by a teleport, a cutscene or combat.
        if (!this.plugin.Navmesh.IsBusy)
        {
            this.IssueMove(this.currentTarget);
        }
    }

    private void IssueMove(TargetSelector.Target target)
    {
        // MoveCloseTo, not MoveTo: landing exactly on the marker is unnecessary and
        // frequently unreachable (the marker sits on a cliff, a platform or inside
        // scenery). Stopping at the edge is what a player would do.
        var ok = this.plugin.Navmesh.MoveCloseTo(target.Position, ArrivalRange);
        if (!ok)
        {
            this.log.Debug("下发寻路指令失败：{Name}", target.Name);
        }
    }

    /// <summary>
    /// Handle the case where the target sits on a different layer of the Occult
    /// Crescent than the player.
    ///
    /// Returns <c>true</c> when this method owns the movement for this tick, so
    /// the caller must not also issue a path. Returning <c>false</c> means the
    /// target is reachable by walking and normal pathing should continue.
    ///
    /// The flow is: walk to a shard on the current layer, then open the network
    /// to a shard on the target layer. Lifestream performs the warp itself, which
    /// matters because it already knows how to drive the shard's menu; doing that
    /// by hand here would break on every client language.
    /// </summary>
    private bool TryHandleCrossLayer(Vector3 playerPosition)
    {
        var shards = this.plugin.Aethernet.Scan(playerPosition);
        if (shards.Count == 0)
        {
            return false;
        }

        var plan = this.plugin.Aethernet.PlanRoute(shards, playerPosition, this.currentTarget.Position);
        if (plan is null || !plan.NeedsTransition)
        {
            return false;
        }

        if (this.warpedForCurrentTarget)
        {
            // Already warped for this target. Let normal pathing take over so we
            // do not ping-pong between shards when the arrival shard happens to
            // be the nearest one on the new layer.
            return false;
        }

        var targetShard = plan.TargetLayerShard!;
        var sourceShard = plan.NearestShard;

        if (sourceShard is null)
        {
            this.StatusText = "找不到可用的以太水晶";
            return true;
        }

        var distanceToShard = sourceShard.DistanceTo(playerPosition);
        if (distanceToShard > AethernetScanner.ShardInteractRange)
        {
            // Walk to the shard on our own layer. Mounting is worth it here: the
            // shards of one layer are spread across a large island.
            this.StatusText =
                $"跨层前往 {this.currentTarget.Name}：先去{AethernetScanner.LayerName(plan.PlayerLayer)}水晶 {sourceShard.Name}";
            this.plugin.UpdateDtr($"Bozja: 前往水晶 {sourceShard.Name}");

            if (this.config.AutoMount)
            {
                this.plugin.Mount.TryMount(distanceToShard, this.config.AutoMountDistance);
            }

            if (!this.plugin.Navmesh.IsBusy)
            {
                this.plugin.Navmesh.MoveCloseTo(sourceShard.Position, AethernetScanner.ShardInteractRange - 1f);
            }

            return true;
        }

        // In range of the shard: ask Lifestream to warp. Try the structured route
        // first (language-independent) and fall back to the display name.
        this.plugin.Mount.Dismount();
        this.plugin.Navmesh.Stop();

        var requested = this.plugin.Lifestream.AethernetTeleportByPlaceNameId(targetShard.PlaceNameId)
                        || this.plugin.Lifestream.AethernetTeleport(targetShard.Name);

        if (!requested)
        {
            // Lifestream refused (busy, or it does not recognise the destination).
            // Do not spin: stop trying for this target and let the normal path
            // attempt happen, then the rescan will pick something else.
            this.warpedForCurrentTarget = true;
            this.activity.Warning($"以太之光传送被拒绝：{targetShard.Name}");
            this.log.Warning("Aethernet teleport refused for {Shard}", targetShard.Name);
            return false;
        }

        this.warpedForCurrentTarget = true;
        this.activity.Success(
            $"以太之光：{AethernetScanner.LayerName(plan.PlayerLayer)} {sourceShard.Name} → " +
            $"{AethernetScanner.LayerName(plan.TargetLayer)} {targetShard.Name}");
        this.log.Information(
            "Aethernet warp {From} -> {To} (layer {FromLayer} -> {ToLayer})",
            sourceShard.Name, targetShard.Name, plan.PlayerLayer, plan.TargetLayer);

        this.StatusText = $"已传送到{AethernetScanner.LayerName(plan.TargetLayer)}";
        return true;
    }

    /// <summary>How close to a FATE/CE marker counts as arrived, in yalms.</summary>
    private const float ArrivalRange = 8f;

    /// <summary>
    /// Position of the target currently being travelled to, or the player's own
    /// position when there is no target. Exposed so the diagnostic command can
    /// report which layer a decision was made against.
    /// </summary>
    public Vector3 CurrentTargetPosition =>
        this.currentTarget.Kind == TargetSelector.TargetKind.None
            ? Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero
            : this.currentTarget.Position;

    public void Dispose()
    {
        this.disposed = true;
        Plugin.Framework.Update -= this.OnFrameworkUpdate;
    }
}
