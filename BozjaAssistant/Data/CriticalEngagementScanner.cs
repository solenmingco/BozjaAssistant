using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Dalamud.Plugin.Services;
using DynamicEvent = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEvent;
using DynamicEventState = FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEventState;

namespace BozjaAssistant.Data;

/// <summary>
/// Read-only view over the "Critical Engagement" (紧急遭遇战) table.
///
/// In the client, both Bozja's Critical Engagements and Occult Crescent's
/// Critical Encounters are "Dynamic Events". The container lives inside the
/// public-content director:
///
///   Bozja            : PublicContentBozja.DynamicEventContainer            @ 0x1388
///   Occult Crescent  : PublicContentOccultCrescent.DynamicEventContainer    @ 0x13B8
///
/// Both directors share <c>PublicContentDirector</c>, so we locate whichever one
/// is currently active and read its 16-slot event array. Layout verified against
/// FFXIVClientStructs <c>InstanceContent/DynamicEventContainer.cs</c>:
///
///   DynamicEventContainer._events        -> FixedSizeArray16&lt;DynamicEvent&gt; @ 0x08
///   DynamicEvent.State                   -> DynamicEventState (byte) @ 0x78
///   DynamicEvent.DynamicEventId          -> ushort @ 0x74
///   DynamicEvent.SecondsLeft             -> uint @ 0x64
///   DynamicEvent.SecondsDuration         -> uint @ 0x68
///   DynamicEvent.Participants            -> byte @ 0x7A
///   DynamicEvent.Progress                -> byte @ 0x7B
///   DynamicEvent.Name                    -> Utf8String @ 0x80
///   DynamicEvent.Description             -> Utf8String @ 0xE8
///   DynamicEvent.MapMarker               -> MapMarkerData @ 0x170
///
/// with
///   enum DynamicEventState : byte { Inactive = 0, Register = 1, Warmup = 2, Battle = 3 }
///
/// "Register" means the CE is open for sign-up, "Warmup" means sign-up closed and
/// it is about to start, "Battle" means it is running (the player may already be
/// inside, or locked out).
/// </summary>
public sealed class CriticalEngagementScanner
{
    private readonly IPluginLog log;

    public CriticalEngagementScanner(IPluginLog log) => this.log = log;

    /// <summary>A single Critical Engagement, flattened into managed memory.</summary>
    public sealed record CeInfo(
        ushort EventId,
        string Name,
        string Description,
        DynamicEventState State,
        uint SecondsLeft,
        uint SecondsDuration,
        byte Participants,
        byte MaxParticipants,
        byte Progress,
        Vector3 Position)
    {
        /// <summary>True while the CE accepts sign-ups.</summary>
        public bool IsRegistering => this.State == DynamicEventState.Register;

        /// <summary>True while the CE is running.</summary>
        public bool IsRunning => this.State == DynamicEventState.Battle;

        /// <summary>True when the player can still usefully travel toward it.</summary>
        public bool IsJoinable => this.State is DynamicEventState.Register or DynamicEventState.Warmup;
    }

    /// <summary>
    /// Snapshot the CE table for whichever public content is currently active.
    /// Returns an empty list when the player is not inside Bozja or Occult Crescent.
    /// </summary>
    public unsafe List<CeInfo> Scan(ContentZone zone)
    {
        var result = new List<CeInfo>();

        var container = GetContainer(zone);
        if (container is null)
        {
            return result;
        }

        // DynamicEventContainer.Events is exposed by FFXIVClientStructs as a
        // Span<DynamicEvent> (verified by reflection over the shipped assembly), so
        // it is indexed directly — no fixed statement and no pointer arithmetic.
        // The span reads straight out of the director's own memory.
        var events = container->Events;

        for (var i = 0; i < events.Length; i++)
        {
            ref var ev = ref events[i];

            try
            {
                if (ev.State == DynamicEventState.Inactive || ev.DynamicEventId == 0)
                {
                    continue;
                }

                // MapMarkerData.Position is a Vector3 field, not three separate
                // floats — verified against FFXIVClientStructs Game/UI/Map.cs.
                var position = ev.MapMarker.Position;
                result.Add(new CeInfo(
                    ev.DynamicEventId,
                    ev.Name.ToString(),
                    ev.Description.ToString(),
                    ev.State,
                    ev.SecondsLeft,
                    ev.SecondsDuration,
                    ev.Participants,
                    ev.MaxParticipants,
                    ev.Progress,
                    position));
            }
            catch (Exception ex)
            {
                this.log.Debug(ex, "读取紧急遭遇战数据失败，已跳过该条目");
            }
        }

        return result;
    }

    /// <summary>
    /// Resolve the DynamicEventContainer for the active public content.
    /// The two directors are distinct types with different member offsets, so we
    /// have to pick the right one based on the detected zone.
    /// </summary>
    private static unsafe DynamicEventContainer* GetContainer(ContentZone zone)
    {
        switch (zone)
        {
            case ContentZone.Bozja:
            {
                var bozja = PublicContentBozja.GetInstance();
                return bozja is null ? null : &bozja->DynamicEventContainer;
            }

            case ContentZone.OccultCrescent:
            {
                var occult = PublicContentOccultCrescent.GetInstance();
                return occult is null ? null : &occult->DynamicEventContainer;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Which special field zone the player is in. Drives every other module,
    /// because the two zones differ in event data and travel rules.
    /// </summary>
    public enum ContentZone
    {
        /// <summary>Not inside a supported zone.</summary>
        None = 0,

        /// <summary>Bozja (博兹雅战线 / 扎杜诺尔高原).</summary>
        Bozja,

        /// <summary>Occult Crescent (新月岛, 7.2+).</summary>
        OccultCrescent,
    }

    /// <summary>
    /// Territory ids for the supported zones. Both the Southern Front and
    /// Zadnor use the Bozja director; the two Occult Crescent maps share the
    /// Occult Crescent director.
    /// </summary>
    private static readonly Dictionary<uint, ContentZone> ZoneByTerritory = new()
    {
        // Bozja: 博兹雅战线 (南方战线) and 扎杜诺尔高原
        { 920, ContentZone.Bozja },
        { 975, ContentZone.Bozja },

        // Occult Crescent: 新月岛 南部 / 北部 (7.2+)
        { 1252, ContentZone.OccultCrescent },
        { 1346, ContentZone.OccultCrescent },
    };

    /// <summary>Map a territory id to a supported zone, or None.</summary>
    public static ContentZone DetectZone(uint territoryTypeId)
        => ZoneByTerritory.TryGetValue(territoryTypeId, out var zone) ? zone : ContentZone.None;

    /// <summary>Human-readable zone name for the UI.</summary>
    public static string ZoneName(ContentZone zone) => zone switch
    {
        ContentZone.Bozja => "博兹雅",
        ContentZone.OccultCrescent => "新月岛",
        _ => "非特殊地图",
    };
}
