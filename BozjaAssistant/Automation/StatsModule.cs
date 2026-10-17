using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace BozjaAssistant.Automation;

/// <summary>
/// 每小时经验 / 战果 / 银金币收益统计。
///
/// 做法是周期性采样「累计值」，再按时间窗口算速率，而不是记账每一次获得——
/// 因为经验来源很多（FATE、CE、小怪、任务），逐条捕获既不可靠也没必要。
/// 采样法只要求总量单调递增，对任何来源都成立。
/// </summary>
public sealed class RateTracker
{
    /// <summary>单个采样点。</summary>
    public sealed record Sample(DateTime AtUtc, long Exp, long Silver, long Gold, long Cluster);

    private const int MaxSamples = 240;

    private readonly IPluginLog log;
    private readonly List<Sample> samples = new();
    private readonly TimeSpan window = TimeSpan.FromMinutes(15);

    public RateTracker(IPluginLog log)
    {
        this.log = log;
    }

    /// <summary>记录一次采样。累计值下降说明换图/切角色，视作重新开始。</summary>
    public void Record(long exp, long silver, long gold, long cluster)
    {
        var now = DateTime.UtcNow;

        if (this.samples.Count > 0)
        {
            var last = this.samples[^1];
            if (exp < last.Exp || silver < last.Silver || gold < last.Gold)
            {
                this.log.Debug("[Rate] 累计值下降，视为换图/切角色，重置采样窗口");
                this.samples.Clear();
            }
        }

        this.samples.Add(new Sample(now, exp, silver, gold, cluster));

        // 只保留窗口内的样本，另加一个窗口外的边界样本，用于计算精确速率。
        while (this.samples.Count > MaxSamples)
            this.samples.RemoveAt(0);

        var cutoff = now - this.window - TimeSpan.FromMinutes(1);
        while (this.samples.Count > 2 && this.samples[0].AtUtc < cutoff)
            this.samples.RemoveAt(0);
    }

    public sealed record Rates(double ExpPerHour, double SilverPerHour, double GoldPerHour, double ClusterPerHour, TimeSpan SpanSeconds, bool HasEnoughData);

    /// <summary>
    /// 算速率。样本不足一分钟时返回 HasEnoughData=false，
    /// 否则刚进图会显示一个天文数字（几十经验除以 0.1 秒）。
    /// </summary>
    public Rates Compute()
    {
        if (this.samples.Count < 2)
            return new Rates(0, 0, 0, 0, TimeSpan.Zero, false);

        var first = this.samples[0];
        var last = this.samples[^1];
        var span = last.AtUtc - first.AtUtc;

        if (span.TotalSeconds < 60)
            return new Rates(0, 0, 0, 0, span, false);

        var hours = span.TotalHours;

        return new Rates(
            (last.Exp - first.Exp) / hours,
            (last.Silver - first.Silver) / hours,
            (last.Gold - first.Gold) / hours,
            (last.Cluster - first.Cluster) / hours,
            span,
            true);
    }

    /// <summary>本次会话累计增益（首末样本之差）。</summary>
    public (long Exp, long Silver, long Gold, long Cluster) SessionDelta()
    {
        if (this.samples.Count < 2)
            return (0, 0, 0, 0);

        var first = this.samples[0];
        var last = this.samples[^1];
        return (last.Exp - first.Exp, last.Silver - first.Silver, last.Gold - first.Gold, last.Cluster - first.Cluster);
    }

    public void Reset() => this.samples.Clear();

    public int SampleCount => this.samples.Count;
}

/// <summary>
/// 每小时收益统计模块。负责从游戏状态里取当前累计值喂给 RateTracker。
/// </summary>
public sealed class StatsModule
{
    private readonly RateTracker tracker;
    private DateTime nextSampleUtc = DateTime.MinValue;

    /// <summary>采样间隔。经验条变化没那么快，1 秒足够，也更省。</summary>
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    public StatsModule(IPluginLog log)
    {
        this.tracker = new RateTracker(log);
    }

    public RateTracker Tracker => this.tracker;

    public bool ShouldSample()
    {
        var now = DateTime.UtcNow;
        if (now < this.nextSampleUtc)
            return false;

        this.nextSampleUtc = now + SampleInterval;
        return true;
    }

    public void Record(long exp, long silver, long gold, long cluster) => this.tracker.Record(exp, silver, gold, cluster);

    public void Reset() => this.tracker.Reset();

    public string Describe()
    {
        var rates = this.tracker.Compute();
        var delta = this.tracker.SessionDelta();

        if (!rates.HasEnoughData)
            return $"统计样本不足（{rates.SpanSeconds.TotalSeconds:F0}s / 需要 60s）";

        var lines = new List<string>
        {
            $"窗口 {(int)rates.SpanSeconds.TotalMinutes} 分 {(int)rates.SpanSeconds.TotalSeconds % 60} 秒",
            $"  经验  {rates.ExpPerHour,12:N0} / 时    本次 +{delta.Exp:N0}",
            $"  银币  {rates.SilverPerHour,12:N0} / 时    本次 +{delta.Silver:N0}",
            $"  金币  {rates.GoldPerHour,12:N0} / 时    本次 +{delta.Gold:N0}",
            $"  战果  {rates.ClusterPerHour,12:N0} / 时    本次 +{delta.Cluster:N0}",
        };

        return string.Join(Environment.NewLine, lines);
    }
}
