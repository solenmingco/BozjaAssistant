using System;
using System.Collections.Generic;
using System.Numerics;
using BozjaAssistant.Data;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

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
    private bool wasDead;
    private bool disposed;

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

        if ((now - this.lastCombatTick).TotalSeconds >= 0.5)
        {
            this.lastCombatTick = now;
            this.plugin.Combat.Tick(playerPosition);
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

        this.currentTarget = TargetSelector.None;
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

        this.activity.Add($"前往 {target.Name}：{target.Reason}");
        this.log.Information("新目标 {Kind} {Name}，{Reason}", target.Kind, target.Name, target.Reason);

        // Start walking immediately rather than waiting for the next tick, so the
        // first step happens as soon as the decision is made.
        this.IssueMove(target);
    }

    /// <summary>Keep the character moving toward the current target.</summary>
    private void ContinueTravel(Vector3 playerPosition)
    {
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

    /// <summary>How close to a FATE/CE marker counts as arrived, in yalms.</summary>
    private const float ArrivalRange = 8f;

    public void Dispose()
    {
        this.disposed = true;
        Plugin.Framework.Update -= this.OnFrameworkUpdate;
    }
}
