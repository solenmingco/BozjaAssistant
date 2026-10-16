using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Dalamud.Plugin.Services;

namespace BozjaAssistant.Data;

/// <summary>
/// Read-only view over the live FATE table.
///
/// Data source: <c>FFXIV.Client.Game.Fate.FateManager</c>, whose <c>Fates</c>
/// member is a <c>StdVector&lt;Pointer&lt;FateContext&gt;&gt;</c> holding every FATE
/// currently known to the client in this territory.
///
/// Layout and offsets verified against FFXIVClientStructs
/// <c>FFXIV/Client/Game/Fate/FateContext.cs</c> and <c>FateManager.cs</c>:
///   FateManager.Instance()               -> FateManager*
///   FateManager.Fates                    -> StdVector&lt;Pointer&lt;FateContext&gt;&gt; @ 0x90
///   FateContext.FateId                   -> ushort  @ 0x18
///   FateContext.Name                     -> Utf8String @ 0xC0
///   FateContext.State                    -> FateState (byte) @ 0x3AC
///   FateContext.Progress                 -> byte @ 0x3B7
///   FateContext.IsBonus                  -> bool @ 0x3C1
///   FateContext.Level / MaxLevel         -> byte @ 0x7F3 / 0x7F4
///   FateContext.Duration                 -> short (seconds) @ 0x28
///   FateContext.StartTimeEpoch           -> int @ 0x20
///   FateContext.Location                 -> Vector3 @ 0x850
///   FateContext.Radius                   -> float @ 0x864
///
/// Only <c>FateState.Running</c> entries are considered actionable; the other
/// states are the client's transitional bookkeeping.
/// </summary>
public sealed class FateScanner
{
    private readonly IPluginLog log;

    public FateScanner(IPluginLog log) => this.log = log;

    /// <summary>One FATE, flattened out of the unmanaged struct into managed memory.</summary>
    public sealed record FateInfo(
        ushort FateId,
        string Name,
        FateState State,
        byte Level,
        byte MaxLevel,
        byte Progress,
        bool IsBonus,
        int DurationSeconds,
        int StartTimeEpoch,
        Vector3 Position,
        float Radius)
    {
        /// <summary>Seconds left before the FATE times out; -1 when not running.</summary>
        public int SecondsLeft
        {
            get
            {
                if (this.DurationSeconds <= 0 || this.StartTimeEpoch <= 0)
                {
                    return -1;
                }

                var elapsed = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - this.StartTimeEpoch;
                return Math.Max(0, this.DurationSeconds - elapsed);
            }
        }

        /// <summary>Progress as a 0-100 percentage.</summary>
        public int ProgressPercent => this.Progress;
    }

    /// <summary>Snapshot every known FATE. Returns an empty list when not in a FATE territory.</summary>
    public unsafe List<FateInfo> Scan(Vector3 playerPosition, bool runningOnly)
    {
        var result = new List<FateInfo>();

        var manager = FateManager.Instance();
        if (manager is null)
        {
            return result;
        }

        var fates = manager->Fates;
        if (fates.First is null)
        {
            return result;
        }

        for (var i = 0; i < fates.Count; i++)
        {
            var context = fates[i].Value;
            if (context is null)
            {
                continue;
            }

            try
            {
                var state = context->State;
                if (runningOnly && state != FateState.Running)
                {
                    continue;
                }

                result.Add(new FateInfo(
                    context->FateId,
                    context->Name.ToString(),
                    state,
                    context->Level,
                    context->MaxLevel,
                    context->Progress,
                    context->IsBonus,
                    context->Duration,
                    context->StartTimeEpoch,
                    context->Location,
                    context->Radius));
            }
            catch (Exception ex)
            {
                // A FATE can be torn down between the pointer read and the field read.
                this.log.Debug(ex, "读取 FATE 数据失败，已跳过该条目");
            }
        }

        return result;
    }
}
