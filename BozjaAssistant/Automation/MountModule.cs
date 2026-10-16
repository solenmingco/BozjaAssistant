using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace BozjaAssistant.Automation;

/// <summary>
/// Mount handling for long-distance travel.
///
/// Design notes:
///  - The mount action is a GeneralAction, not a normal action. Verified against
///    the game's own GeneralAction sheet: row 9 is "Mount Roulette" and row 24 is
///    "Flying Mount Roulette"; there is no generic "Mount" row. Row 23 is Dismount.
///    Using the roulette rows is what a player's hotbar does, and it respects the
///    player's own mount list instead of hardcoding a mount id.
///  - ActionType.GeneralAction is 5 and ActionType.Mount is 13 (see the enum in
///    FFXIVClientStructs ActionManager.cs). We use GeneralAction for summoning and
///    dismounting, because that is what the hotbar action does.
///  - Summoning cannot be spammed. The game refuses the action while already
///    mounted, while mounting, in combat, or mid-cast, and repeated failed attempts
///    look like a stuck loop. So every attempt is gated on a condition check and
///    followed by a cooldown, and only the transition into "mounted" is reported.
/// </summary>
public sealed class MountModule : IDisposable
{
    /// <summary>GeneralAction row for "Mount Roulette".</summary>
    private const uint MountRouletteAction = 9;

    /// <summary>GeneralAction row for "Dismount".</summary>
    private const uint DismountAction = 23;

    /// <summary>Do not retry a summon more often than this.</summary>
    private static readonly TimeSpan AttemptCooldown = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the summon to complete before giving up on it.</summary>
    private static readonly TimeSpan SummonTimeout = TimeSpan.FromSeconds(6);

    private readonly IObjectTable objectTable;
    private readonly ICondition condition;
    private readonly ActivityLog activity;
    private readonly IPluginLog log;

    private DateTime lastAttempt = DateTime.MinValue;
    private DateTime summonStarted = DateTime.MinValue;
    private bool isSummoning;
    private bool wasMounted;

    public MountModule(IObjectTable objectTable, ICondition condition, ActivityLog activity, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.condition = condition;
        this.activity = activity;
        this.log = log;
    }

    /// <summary>True while the player is on a mount (or in the mount transition).</summary>
    public bool IsMounted => this.condition[ConditionFlag.Mounted]
                             || this.condition[ConditionFlag.Mounting]
                             || this.condition[ConditionFlag.MountOrOrnamentTransition];

    /// <summary>True while the character cannot act (casting, cutscene, loading).</summary>
    private bool IsBusy =>
        this.condition[ConditionFlag.Casting]
        || this.condition[ConditionFlag.BetweenAreas]
        || this.condition[ConditionFlag.OccupiedInQuestEvent]
        || this.condition[ConditionFlag.WatchingCutscene]
        || this.condition[ConditionFlag.WatchingCutscene78];

    /// <summary>Mounts are unavailable in combat, so we simply do not try.</summary>
    private bool InCombat => this.condition[ConditionFlag.InCombat];

    /// <summary>
    /// Try to mount up. Safe to call every tick: it decides for itself whether an
    /// attempt is warranted, and returns true once the player is actually mounted.
    /// </summary>
    /// <param name="distanceToTarget">Distance to the current travel target, in yalms.</param>
    /// <param name="threshold">Only mount when the target is at least this far away.</param>
    public bool TryMount(float distanceToTarget, float threshold)
    {
        if (distanceToTarget < threshold)
        {
            return false;
        }

        if (this.IsMounted)
        {
            this.MarkMounted();
            return true;
        }

        // Out of combat only: the action would be rejected anyway, and a rejected
        // action is indistinguishable from a missing one in the log.
        if (this.InCombat || this.IsBusy)
        {
            return false;
        }

        // A summon that never resolves (interrupted, zoned, target moved) must not
        // latch us into "isSummoning" forever.
        if (this.isSummoning && DateTime.UtcNow - this.summonStarted > SummonTimeout)
        {
            this.isSummoning = false;
            this.log.Debug("上坐骑超时，允许重试。");
        }

        if (this.isSummoning)
        {
            return false;
        }

        if (DateTime.UtcNow - this.lastAttempt < AttemptCooldown)
        {
            return false;
        }

        this.lastAttempt = DateTime.UtcNow;
        this.summonStarted = DateTime.UtcNow;
        this.isSummoning = true;

        var ok = this.UseGeneralAction(MountRouletteAction);
        if (ok)
        {
            this.log.Debug("已尝试上坐骑（距离目标 {Distance:F0} 米）。", distanceToTarget);
        }
        else
        {
            this.isSummoning = false;
            this.log.Debug("上坐骑指令被游戏拒绝。");
        }

        return false;
    }

    /// <summary>Dismount, used when arriving at the target.</summary>
    public void Dismount()
    {
        if (!this.IsMounted)
        {
            return;
        }

        // Mounting/unmounting transitions reject the action; wait for a settled state.
        if (this.condition[ConditionFlag.Mounting] || this.condition[ConditionFlag.MountOrOrnamentTransition])
        {
            return;
        }

        this.UseGeneralAction(DismountAction);
    }

    /// <summary>
    /// Record the transition into "mounted" exactly once, so the activity log is
    /// not spammed while riding. Both the tick and the summon path funnel here.
    /// </summary>
    private void MarkMounted()
    {
        if (this.wasMounted)
        {
            return;
        }

        this.isSummoning = false;
        this.wasMounted = true;
        this.activity.Add("已上坐骑。");
    }

    /// <summary>Detect the mounted transitions so they are reported exactly once.</summary>
    public void Tick()
    {
        if (this.IsMounted)
        {
            this.MarkMounted();
        }
        else if (this.wasMounted)
        {
            this.wasMounted = false;
            this.activity.Add("已下坐骑。");
        }
    }

    /// <summary>
    /// Invoke a GeneralAction. The manager owns the target id; 0xE000_0000 is the
    /// "no target" sentinel that the game itself uses for self-targeted actions.
    /// </summary>
    private unsafe bool UseGeneralAction(uint actionId)
    {
        try
        {
            var manager = ActionManager.Instance();
            if (manager is null)
            {
                return false;
            }

            return manager->UseAction(ActionType.GeneralAction, actionId);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "调用上坐骑动作时发生异常");
            return false;
        }
    }

    /// <summary>Stop tracking mount state; used on emergency stop.</summary>
    public void Reset()
    {
        this.isSummoning = false;
        this.wasMounted = this.IsMounted;
    }

    public void Dispose()
    {
    }
}
