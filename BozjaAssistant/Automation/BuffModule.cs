using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace BozjaAssistant.Automation;

/// <summary>
/// 幻影职业增益自动施放。
///
/// 新月岛的幻影职业（Phantom Job）切换是即时的，所以可以「切过去 → 放增益 → 切回来」，
/// 用几个低冷却的增益刷一身 Buff 再回到本来职业继续战斗。这正是 bozjalone 的 buff 模块在做的事。
///
/// 状态 ID 与技能 ID 的对应关系是从游戏表里核出来的（不是猜的）：
///   Enduring Fortitude 4233 ← Phantom Knight 的 Pray (41589)
///   Fleetfooted         4239 ← Phantom Monk 的 Phantom Kick (41595)
///   Romeo's Ballad      4244 ← Phantom Bard 的 Romeo's Ballad (41609)
///   Quicker Step        4799 ← Phantom Dancer 的 Quickstep (46603)
/// 每个增益的状态 ID 都排在对应职业技能的状态区段里，四组全部吻合。
///
/// 「探求之心」(Inquiring Mind, 46606) 是 Phantom Freelancer 的一个技能，能一次给
/// 多个增益，所以用它比逐个切职业更快；但它的 Buff 覆盖不如逐个切全，因此作为可选。
/// </summary>
public sealed class BuffModule
{
    /// <summary>一个可自动维持的增益。</summary>
    public sealed record BuffSpec(
        string Name,
        uint StatusId,
        byte SupportJobId,
        uint ActionId,
        /// <summary>增益的持续时间（秒）。用于判断何时该续。取的是技能描述的保守值。</summary>
        int DurationSeconds);

    /// <summary>
    /// 幻影职业在地图上的 SupportJob 编号（来自 MKDSupportJob 表的行号）。
    /// 注意行号不是连续的，1 是 Knight 但表里没有同名的 Phantom Knight 条目，
    /// 因此下面用「技能所在职业」来定位而不是靠名称。
    /// </summary>
    private const byte JobKnight = 1;
    private const byte JobMonk = 3;
    private const byte JobBard = 6;
    private const byte JobDancer = 15;

    private static readonly BuffSpec[] Specs =
    {
        new("不屈之躯 (Enduring Fortitude)", 4233, JobKnight, 41589, 300),
        new("迅足 (Fleetfooted)", 4239, JobMonk, 41595, 300),
        new("罗密欧的歌谣 (Romeo's Ballad)", 4244, JobBard, 41609, 300),
        new("轻快舞步 (Quicker Step)", 4799, JobDancer, 46603, 300),
    };

    private readonly IPluginLog log;

    /// <summary>切换职业后需要等一帧以上游戏才会认这个职业，用一个短计时器兜住。</summary>
    private DateTime jobSwitchReadyAtUtc = DateTime.MinValue;

    /// <summary>我们切走之前原本的职业，最后要切回来。</summary>
    private byte originalJob;

    /// <summary>
    /// 一次增益施放的阶段。分开记是因为「切过去 → 放技能 → 切回来」要跨多个 tick：
    /// 切过去后游戏需要一拍才认职业，放完技能又要切回去。
    /// 单靠一个 nullable 字段无法表达这三种状态，之前的实现会在这里丢掉原职业。
    /// </summary>
    public enum ApplyStage
    {
        Idle = 0,
        SwitchedToBuffJob,
        Casting,
        RestoringOriginal,
    }

    private ApplyStage phase = ApplyStage.Idle;

    /// <summary>当前阶段，控制器按阶段推进流程。</summary>
    public ApplyStage Phase => this.phase;

    /// <summary>本次要施放的目标增益，用于日志与超时判断。</summary>
    private BuffSpec? activeSpec;

    /// <summary>整个动作的超时保护：万一某个 IPC 没回，不能永久卡在非 Idle 状态。</summary>
    private DateTime applyDeadlineUtc = DateTime.MinValue;

    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(5);

    public BuffModule(IPluginLog log)
    {
        this.log = log;
    }

    public static IReadOnlyList<BuffSpec> AllSpecs => Specs;

    /// <summary>按配置返回应当维持的增益清单。</summary>
    public static List<BuffSpec> EnabledSpecs(Configuration config)
    {
        var result = new List<BuffSpec>();
        foreach (var spec in Specs)
        {
            var enabled = spec.SupportJobId switch
            {
                JobKnight => config.BuffEnduringFortitude,
                JobMonk => config.BuffFleetfooted,
                JobBard => config.BuffRomeosBallad,
                JobDancer => config.BuffQuickerStep,
                _ => false,
            };

            if (enabled)
                result.Add(spec);
        }

        return result;
    }

    /// <summary>
    /// 判断是否该动手。返回需要施放的增益，或 null 表示暂时什么都不用做。
    /// 检查的是「已有状态的剩余时间低于阈值」，而不是「完全没有」——Buff 快到期时就续，
    /// 而不是等它掉光，这样战斗中不会露出空窗。
    /// </summary>
    public BuffSpec? PickDueBuff(Configuration config, IReadOnlyCollection<(uint StatusId, float RemainingSeconds)> activeBuffs, byte currentJob)
    {
        if (!config.BuffEnabled)
            return null;

        // 正在切换/施放中，别插队。
        if (this.phase != ApplyStage.Idle)
        {
            // 超时兜底：正常一次施放几百毫秒内就该结束。
            if (DateTime.UtcNow > this.applyDeadlineUtc)
            {
                this.log.Warning("[Buff] 上次施放超时，强制复位");
                this.Reset();
            }
            else
            {
                return null;
            }
        }

        var threshold = Math.Max(1, config.BuffReapplyThresholdMinutes) * 60f;
        var due = new List<BuffSpec>();

        foreach (var spec in EnabledSpecs(config))
        {
            var existing = activeBuffs.FirstOrDefault(b => b.StatusId == spec.StatusId);
            if (existing.StatusId == 0)
            {
                // 完全没有这个增益。
                due.Add(spec);
                continue;
            }

            if (existing.RemainingSeconds <= threshold)
                due.Add(spec);
        }

        if (due.Count == 0)
            return null;

        // 优先选「现在就在这个职业上」的，省一次来回切换。
        var alreadyHere = due.FirstOrDefault(s => s.SupportJobId == currentJob);
        if (alreadyHere is not null)
            return alreadyHere;

        return due[0];
    }

    /// <summary>
    /// 发起一次增益施放：切职业、记下原职业、设定就绪时间。
    /// 返回 true 表示已发出切换请求。
    /// </summary>
    public unsafe bool BeginApply(BuffSpec spec, byte currentJob)
    {
        if (this.phase != ApplyStage.Idle)
            return false;

        this.originalJob = currentJob;
        this.activeSpec = spec;
        this.applyDeadlineUtc = DateTime.UtcNow + ApplyTimeout;

        if (currentJob != spec.SupportJobId)
        {
            if (!PublicContentOccultCrescent.ChangeSupportJob(spec.SupportJobId))
            {
                this.log.Warning($"[Buff] 切换到幻影职业 {spec.SupportJobId} 失败");
                this.Reset();
                return false;
            }

            this.phase = ApplyStage.SwitchedToBuffJob;
            // 职业切换后游戏需要一拍才认，220ms 足够且不会拖慢循环。
            this.jobSwitchReadyAtUtc = DateTime.UtcNow.AddMilliseconds(220);
            return true;
        }

        // 已经在目标职业上，不需要切换。
        this.phase = ApplyStage.Casting;
        this.jobSwitchReadyAtUtc = DateTime.UtcNow;
        return true;
    }

    /// <summary>切换是否已经生效、可以放技能了。</summary>
    public bool IsReadyToCast => DateTime.UtcNow >= this.jobSwitchReadyAtUtc;

    /// <summary>当前所处的阶段，供界面与诊断显示。</summary>
    public string PhaseText => this.phase switch
    {
        ApplyStage.Idle => "空闲",
        ApplyStage.SwitchedToBuffJob => "已切到增益职业",
        ApplyStage.Casting => "施放中",
        ApplyStage.RestoringOriginal => "切回原职业",
        _ => "未知",
    };

    /// <summary>
    /// 标记「技能已经放出去了」，下一步该切回原职业。
    /// 分两步而不是在 EndApply 里一把清空，是因为切回原职业本身要等一个 tick，
    /// 期间必须记得原职业是什么。
    /// </summary>
    public unsafe void FinishCast()
    {
        if (this.phase != ApplyStage.Casting && this.phase != ApplyStage.SwitchedToBuffJob)
            return;

        if (this.originalJob == 0)
        {
            this.Reset();
            return;
        }

        if (!PublicContentOccultCrescent.ChangeSupportJob(this.originalJob))
        {
            this.log.Warning($"[Buff] 切回幻影职业 {this.originalJob} 失败");
            this.Reset();
            return;
        }

        this.phase = ApplyStage.RestoringOriginal;
        this.jobSwitchReadyAtUtc = DateTime.UtcNow.AddMilliseconds(220);
    }

    /// <summary>整个动作完成，回到空闲并明确记录原职业已恢复。</summary>
    public void EndApply()
    {
        if (this.phase == ApplyStage.RestoringOriginal && DateTime.UtcNow >= this.jobSwitchReadyAtUtc)
        {
            this.log.Debug($"[Buff] {this.activeSpec?.Name} 施放完成，已切回职业 {this.originalJob}");
            this.originalJob = 0;
            this.activeSpec = null;
            this.phase = ApplyStage.Idle;
            this.applyDeadlineUtc = DateTime.MinValue;
        }
    }

    /// <summary>状态异常、死亡、离开地图时强制清空中间状态，避免卡在「切换中」。</summary>
    public void Reset()
    {
        this.phase = ApplyStage.Idle;
        this.jobSwitchReadyAtUtc = DateTime.MinValue;
        this.originalJob = 0;
        this.activeSpec = null;
        this.applyDeadlineUtc = DateTime.MinValue;
    }

    /// <summary>当前幻影职业编号。0 表示没有（不在新月岛或未持有幻影职业）。</summary>
    public static unsafe byte GetCurrentJob()
    {
        var state = PublicContentOccultCrescent.GetState();
        return state is null ? (byte)0 : state->CurrentSupportJob;
    }

    /// <summary>
    /// 读取玩家身上的增益剩余时间。
    /// 走 Dalamud 的 StatusList 而不是直接读内存，因为状态列表的偏移在各版本都变过。
    /// </summary>
    public static List<(uint StatusId, float RemainingSeconds)> ReadActiveBuffs(IEnumerable<(uint StatusId, float RemainingTime)> statuses, IReadOnlyList<BuffSpec> watch)
    {
        var wanted = new HashSet<uint>(watch.Select(w => w.StatusId));
        var result = new List<(uint, float)>();

        foreach (var s in statuses)
        {
            if (wanted.Contains(s.StatusId))
                result.Add((s.StatusId, s.RemainingTime));
        }

        return result;
    }

    /// <summary>诊断用：把四个增益的当前状态打出来。</summary>
    public static string Describe(Configuration config, IReadOnlyList<(uint StatusId, float RemainingSeconds)> active)
    {
        var lines = new List<string>();
        foreach (var spec in Specs)
        {
            var enabled = EnabledSpecs(config).Contains(spec);
            var hit = active.FirstOrDefault(a => a.StatusId == spec.StatusId);
            var state = hit.StatusId == 0 ? "无" : $"{hit.RemainingSeconds:F0}s";
            lines.Add($"  {(enabled ? "[启用]" : "[关闭]")} {spec.Name}  状态{spec.StatusId}  剩余 {state}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    // 保留引用以免 IDE 提示未使用；实际读取由调用方传入。
    internal static IReadOnlyList<BuffSpec> WatchList => Specs;
}
