using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BozjaAssistant.Data;
using static BozjaAssistant.Data.CriticalEngagementScanner;
using DynamicEventState = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEventState;

namespace BozjaAssistant.Automation;

/// <summary>
/// Decides what to travel to next.
///
/// This is deliberately a pure function of the scanned world state plus the
/// configuration: it never calls IPC, never mutates anything and holds no state
/// between calls, so it is trivially testable and cannot desync from the game.
/// The state machine in <see cref="AutomationController"/> owns acting on it.
/// </summary>
public static class TargetSelector
{
    /// <summary>A destination chosen for travel, with the reason it won.</summary>
    public sealed record Target(
        TargetKind Kind,
        string Name,
        Vector3 Position,
        float Distance,
        string Reason);

    public enum TargetKind
    {
        None,
        Fate,
        CriticalEngagement,
    }

    public static readonly Target None = new(TargetKind.None, string.Empty, Vector3.Zero, 0f, string.Empty);

    /// <summary>
    /// Pick a Critical Engagement worth travelling to, or null when none qualify.
    /// CEs are evaluated first because they are time-boxed and give the best rewards.
    /// </summary>
    public static Target? SelectCe(
        IReadOnlyList<CeInfo> engagements,
        Vector3 playerPosition,
        Configuration config)
    {
        if (!config.CeEnabled)
        {
            return null;
        }

        var best = engagements
            .Where(ce => (config.CeJoinOnRegister && ce.IsRegistering)
                         || (config.CeJoinOnWarmup && ce.State == DynamicEventState.Warmup))
            .Where(ce => !IsBlacklisted(ce, config))
            .Select(ce => (ce, distance: Vector3.Distance(playerPosition, ce.Position)))
            .Where(x => config.CeMaxDistance <= 0f || x.distance <= config.CeMaxDistance)
            .OrderBy(x => x.distance)
            .ToList();

        if (best.Count == 0)
        {
            return null;
        }

        // Warm-up CEs are preferred over merely-registering ones at comparable
        // distance: their sign-up window is the one about to close, so arriving
        // late is the failure mode worth avoiding.
        var bestChoice = best
            .OrderBy(x => x.ce.State == DynamicEventState.Warmup ? 0 : 1)
            .ThenBy(x => x.distance)
            .First();

        var (chosen, distance) = bestChoice;

        var stateText = chosen.IsRegistering ? "报名中" : "准备中";
        return new Target(
            TargetKind.CriticalEngagement,
            chosen.Name,
            chosen.Position,
            distance,
            $"紧急遭遇战（{stateText}，{distance:F0} 米，{chosen.SecondsLeft}s 后结束）");
    }

    /// <summary>
    /// Pick a FATE to travel to, or null when none qualify. The priority mode
    /// decides the ordering; the filters are applied first so a "best" candidate
    /// is never one we would refuse to run.
    /// </summary>
    public static Target? SelectFate(
        IReadOnlyList<FateScanner.FateInfo> fates,
        Vector3 playerPosition,
        Configuration config)
    {
        if (!config.FateEnabled)
        {
            return null;
        }

        var candidates = fates
            .Where(f => f.State == FFXIVClientStructs.FFXIV.Client.Game.Fate.FateState.Running)
            .Select(f => (fate: f, distance: Vector3.Distance(playerPosition, f.Position)))
            .Where(x => config.FateMaxDistance <= 0f || x.distance <= config.FateMaxDistance)
            .Where(x => config.FateSkipProgressAbove <= 0 || x.fate.ProgressPercent < config.FateSkipProgressAbove)
            .Where(x => config.FateSkipSecondsLeftBelow <= 0
                        || x.fate.SecondsLeft < 0
                        || x.fate.SecondsLeft > config.FateSkipSecondsLeftBelow)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // Bonus FATEs are worth more, so they float to the top unless disabled.
        var ordered = candidates
            .OrderByDescending(x => config.FatePreferBonus && x.fate.IsBonus)
            .ThenBy(x => config.FatePriority switch
            {
                FatePriorityMode.LowestProgress => x.fate.ProgressPercent,
                FatePriorityMode.LeastTimeLeft => x.fate.SecondsLeft < 0 ? int.MaxValue : x.fate.SecondsLeft,
                FatePriorityMode.HighestLevel => -x.fate.Level,
                _ => x.distance,
            })
            .ToList();

        var (chosen, distance) = ordered[0];

        var notes = new List<string> { $"{distance:F0} 米" };
        if (chosen.IsBonus)
        {
            notes.Add("加成");
        }

        if (chosen.SecondsLeft >= 0)
        {
            notes.Add($"剩余 {chosen.SecondsLeft}s");
        }

        notes.Add($"进度 {chosen.ProgressPercent}%");

        return new Target(
            TargetKind.Fate,
            chosen.Name,
            chosen.Position,
            distance,
            $"FATE（{string.Join("，", notes)}）");
    }

    private static bool IsBlacklisted(CeInfo ce, Configuration config)
    {
        foreach (var entry in config.CeBlacklist)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (ce.Name.Contains(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
