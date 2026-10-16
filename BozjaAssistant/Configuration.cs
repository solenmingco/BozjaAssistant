using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace BozjaAssistant;

/// <summary>
/// Persisted plugin configuration. Dalamud stores this as JSON in the plugin
/// config directory.
///
/// These are public *fields* rather than auto-properties on purpose: ImGui's
/// widget bindings take <c>ref</c> parameters, and C# forbids passing a
/// non-ref-returning property by reference. Fields keep the UI code free of the
/// temporary-variable dance that each setting would otherwise need, which is the
/// difference between adding a setting in one line and adding it in five.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    /// <summary>
    /// Config schema version. Must stay a property: <see cref="IPluginConfiguration"/>
    /// declares it, and a field cannot implement an interface member.
    /// </summary>
    public int Version { get; set; } = 1;

    // ---------------------------------------------------------------- 6 总控
    /// <summary>Master switch. When false every module stays idle.</summary>
    public bool AutomationEnabled;

    /// <summary>
    /// Emergency stop. While true nothing runs, and it must be cleared manually
    /// in the UI — a deliberate speed bump.
    /// </summary>
    public bool EmergencyStop;

    /// <summary>Virtual-key code for the panic hotkey. Default: F12 (123).</summary>
    public int PanicHotkey = 123;

    /// <summary>Show a DTR (server info bar) entry with the current status.</summary>
    public bool ShowDtrEntry = true;

    /// <summary>Write verbose diagnostics to the Dalamud log.</summary>
    public bool VerboseLogging;

    /// <summary>Only operate inside Bozja / Occult Crescent. Strongly recommended.</summary>
    public bool OnlyInSupportedZones = true;

    // ------------------------------------------------------------ 1 FATE 模块
    public bool FateEnabled = true;

    /// <summary>How the next FATE is chosen when several are running.</summary>
    public FatePriorityMode FatePriority = FatePriorityMode.Nearest;

    /// <summary>Ignore FATEs further away than this many yalms. 0 = no limit.</summary>
    public float FateMaxDistance = 600f;

    /// <summary>Ignore FATEs at or above this progress percentage. 0 = disabled.</summary>
    public int FateSkipProgressAbove = 90;

    /// <summary>Ignore FATEs with less than this many seconds left. 0 = disabled.</summary>
    public int FateSkipSecondsLeftBelow = 60;

    /// <summary>Re-scan and pick a new target this often (seconds).</summary>
    public int FateRescanSeconds = 5;

    /// <summary>Treat "bonus" FATEs as higher priority.</summary>
    public bool FatePreferBonus = true;

    // ------------------------------------------------------- 2 紧急遭遇战 CE
    public bool CeEnabled = true;

    /// <summary>Travel to a CE as soon as it opens for registration.</summary>
    public bool CeJoinOnRegister = true;

    /// <summary>Keep travelling during the warm-up window.</summary>
    public bool CeJoinOnWarmup = true;

    /// <summary>Ignore CEs further away than this many yalms. 0 = no limit.</summary>
    public float CeMaxDistance = 900f;

    /// <summary>Prioritise CEs over FATEs when both are available.</summary>
    public bool CeOverridesFate = true;

    /// <summary>CE names (or substrings) the player never wants to join.</summary>
    public List<string> CeBlacklist = new();

    // ------------------------------------------------------------ 3 掉落提醒
    public bool DropAlertEnabled = true;

    /// <summary>Item ids that trigger an alert when obtained.</summary>
    public List<uint> DropAlertItemIds = new();

    /// <summary>Play a sound when an alerted item is obtained.</summary>
    public bool DropAlertSound = true;

    /// <summary>Dalamud chat sound id used for the alert.</summary>
    public int DropAlertSoundId = 9;

    /// <summary>Show a chat line when an alerted item is obtained.</summary>
    public bool DropAlertChat = true;

    // ------------------------------------------------------------ 4 自动战斗
    public bool CombatEnabled;

    /// <summary>Auto-target the nearest hostile when idle in combat range.</summary>
    public bool AutoTarget = true;

    /// <summary>Do not engage enemies further than this many yalms.</summary>
    public float EngageRange = 25f;

    /// <summary>Automatically run the rotation through an external AI plugin.</summary>
    public bool ExternalCombatAi;

    /// <summary>Which external rotation plugin to drive, if any.</summary>
    public ExternalAiProvider ExternalAi = ExternalAiProvider.None;

    // ----------------------------------------------------------- 5 通用自动化
    /// <summary>Automatically accept the revive prompt after dying.</summary>
    public bool AutoRevive = true;

    /// <summary>Seconds to wait before accepting a revive.</summary>
    public int AutoReviveDelaySeconds = 3;

    /// <summary>Automatically walk back to the FATE/CE area after reviving.</summary>
    public bool AutoReturnAfterRevive = true;

    /// <summary>Automatically mount up when travelling a long distance.</summary>
    public bool AutoMount;

    /// <summary>Distance beyond which the plugin mounts before travelling.</summary>
    public float AutoMountDistance = 60f;

    // ---------------------------------------------------------------- 统计
    public int TotalFatesCompleted;
    public int TotalCesJoined;
    public int TotalDropsAlerted;

    /// <summary>Unix seconds of the last completed automation cycle.</summary>
    public long LastRunUnixSeconds;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

/// <summary>Strategy used to pick which running FATE to travel to.</summary>
public enum FatePriorityMode
{
    /// <summary>Closest to the player.</summary>
    Nearest,

    /// <summary>Least progress — likely to still be running when we arrive.</summary>
    LowestProgress,

    /// <summary>Closest to timing out — most urgent.</summary>
    LeastTimeLeft,

    /// <summary>Highest level first.</summary>
    HighestLevel,
}

/// <summary>External rotation / combat AI plugin this plugin can drive over IPC.</summary>
public enum ExternalAiProvider
{
    /// <summary>Do not drive any external rotation plugin.</summary>
    None,

    /// <summary>Rotation Solver Reborn.</summary>
    RotationSolverReborn,

    /// <summary>WrathCombo.</summary>
    WrathCombo,
}
