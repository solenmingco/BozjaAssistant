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
    public bool AutoMount = true;

    /// <summary>Distance beyond which the plugin mounts before travelling.</summary>
    public float AutoMountDistance = 60f;

    /// <summary>
    /// Use the internal Aethernet shards to cross layers of the Occult Crescent
    /// (新月岛), whose three stacked layers have no ground connection. Without
    /// this, targets on another layer are simply unreachable by walking.
    /// </summary>
    public bool AethernetRouting = true;

    /// <summary>Seconds to wait for the warp to complete before giving up.</summary>
    public int AethernetWarpTimeoutSeconds = 20;

    // -------------------------------------------------------- 6 宝箱与胡萝卜
    /// <summary>启用宝箱 / 胡萝卜采集。</summary>
    public bool HuntEnabled;

    /// <summary>优先前往对象表里真实存在的宝箱 / 胡萝卜。</summary>
    public bool HuntPreferLiveObjects = true;

    /// <summary>
    /// 对象表里没有目标时，是否按候选点巡逻。
    /// 关掉的话，只在真的看到目标时才动，不会白跑候选点。
    /// </summary>
    public bool HuntPatrolCandidates = true;

    /// <summary>巡逻时是否也走宝箱候选点。</summary>
    public bool HuntPatrolTreasure = true;

    /// <summary>巡逻时是否也走胡萝卜候选点。</summary>
    public bool HuntPatrolCarrot;

    /// <summary>超过这个距离的目标不考虑。0 = 不限。</summary>
    public float HuntMaxDistance = 400f;

    // ------------------------------------------------------------ 7 刷怪
    public bool MobFarmerEnabled;

    /// <summary>拉怪半径。</summary>
    public float MobFarmerRange = 30f;

    /// <summary>聚够这么多只才开始拉。</summary>
    public int MobFarmerMinCount = 3;

    /// <summary>进入这个距离就停止移动，开始输出。</summary>
    public float MobFarmerEngageDistance = 12f;

    /// <summary>只拉不高于玩家这么多级的怪。0 = 不限。</summary>
    public int MobFarmerMaxLevelDelta = 3;

    /// <summary>拉怪时用飞行坐骑。</summary>
    public bool MobFarmerFly;

    // -------------------------------------------------------- 8 幻影职业 Buff
    public bool BuffEnabled;

    public bool BuffEnduringFortitude;
    public bool BuffFleetfooted;
    public bool BuffRomeosBallad;
    public bool BuffQuickerStep;

    /// <summary>剩余时间低于这么多分钟时就续。</summary>
    public int BuffReapplyThresholdMinutes = 2;

    // ------------------------------------------------------------ 9 两歧塔
    public bool ForkedTowerEnabled;

    /// <summary>陷阱在这个距离内才记录（米）。</summary>
    public float ForkedTowerMaxDistance = 80f;

    /// <summary>检测到陷阱时写一条日志。</summary>
    public bool ForkedTowerAlert = true;

    // ------------------------------------------------------------ 10 统计
    /// <summary>启用每小时收益统计。</summary>
    public bool StatsEnabled = true;

    /// <summary>自动返回到营地（离开战斗、无事可做时）。</summary>
    public bool AutoReturnToCamp;

    /// <summary>返回营地用的传送目的地 PlaceNameId。0 = 用 BaseCamp。</summary>
    public uint ReturnCampPlaceNameId = 4944;

    // -------------------------------------------------------- 逐活动白名单
    /// <summary>
    /// 只前往名字里含这些关键词的 FATE。为空表示全部允许。
    /// 对应 bozjalone 的逐活动开关，但用关键词而不是硬编码 id —— 国服 id 与国际服不同。
    /// </summary>
    public List<string> FateAllowList = new();

    /// <summary>只前往名字里含这些关键词的 CE。为空表示全部允许。</summary>
    public List<string> CeAllowList = new();

    /// <summary>发现可参加的 CE 后延迟这么多秒再动身（对应 DelayCriticalEncounters）。</summary>
    public int CeStartDelaySeconds;

    // ---------------------------------------------------------------- 统计
    public int TotalFatesCompleted;
    public int TotalCesJoined;
    public int TotalDropsAlerted;
    public int TotalTreasuresLooted;

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

    /// <summary>
    /// BossMod Reborn —— 严格说它管的是机制处理而不是输出循环，
    /// 但它的 AI 模块确实会接管走位与技能，因此与 RSR/WrathCombo 并列为可选联动对象。
    /// </summary>
    BossModReborn,
}
