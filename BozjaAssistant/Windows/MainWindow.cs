using System;
using System.Numerics;
using BozjaAssistant.Data;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BozjaAssistant.Windows;

/// <summary>
/// The main control window: a live dashboard of what the automation is doing,
/// plus the switches the user actually reaches for while playing.
///
/// The layout is deliberately compact and scroll-free at default size: this window
/// gets opened mid-FATE, so anything that needs hunting for is a failure.
/// </summary>
public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration config;

    public MainWindow(Plugin plugin)
        : base("Bozja / 新月岛 助手###BozjaAssistantMain")
    {
        this.plugin = plugin;
        this.config = plugin.Configuration;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.Size = new Vector2(560, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        this.DrawStatusHeader();

        ImGui.Separator();

        using (var tabs = ImRaii.TabBar("##bozja-tabs"))
        {
            if (!tabs)
            {
                return;
            }

            this.DrawTab("总览", FontAwesomeIcon.Gauge, this.DrawOverviewTab);
            this.DrawTab("掉落提醒", FontAwesomeIcon.Bell, this.DrawDropTab);
            this.DrawTab("日志", FontAwesomeIcon.List, this.DrawLogTab);
            this.DrawTab("关于", FontAwesomeIcon.InfoCircle, this.DrawAboutTab);
        }
    }

    /// <summary>The always-visible top strip: zone, state and the master switch.</summary>
    private void DrawStatusHeader()
    {
        var automation = this.plugin.Automation;
        var zone = CriticalEngagementScanner.DetectZone(Plugin.ClientState.TerritoryType);

        ImGui.TextColored(ImGuiColors.DalamudGrey, "当前地图：");
        ImGui.SameLine();
        ImGui.TextColored(
            zone == CriticalEngagementScanner.ContentZone.None ? ImGuiColors.DalamudGrey : ImGuiColors.ParsedGreen,
            CriticalEngagementScanner.ZoneName(zone));

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, "状态：");
        ImGui.SameLine();
        ImGui.TextUnformatted(automation.StatusText);

        if (this.config.EmergencyStop)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "紧急停止生效中 — 需手动解除");
            if (ImGui.Button("解除紧急停止"))
            {
                this.config.EmergencyStop = false;
                this.config.Save();
                this.plugin.Activity.Add("紧急停止已解除。");
            }

            ImGui.SameLine();
        }

        var running = this.config.AutomationEnabled && !this.config.EmergencyStop;
        if (running)
        {
            if (ImGui.Button("停止"))
            {
                automation.Stop();
            }
        }
        else
        {
            if (ImGui.Button("开始"))
            {
                automation.Start();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("紧急停止"))
        {
            automation.EmergencyStop();
        }

        ImGui.SameLine();
        if (ImGui.Button("设置"))
        {
            this.plugin.ToggleConfigUi();
        }
    }

    private void DrawTab(string label, FontAwesomeIcon icon, Action draw)
    {
        using var tab = ImRaii.TabItem($"{icon.ToIconString()} {label}##tab-{label}");
        if (tab)
        {
            draw();
        }
    }

    private void DrawOverviewTab()
    {
        var player = Plugin.ObjectTable.LocalPlayer;

        if (player is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "尚未进入游戏。");
            return;
        }

        var playerPosition = player.Position;

        // ---- live counts
        ImGui.TextUnformatted("实时状态");
        ImGui.Separator();

        var fates = this.plugin.Fates.Scan(playerPosition, runningOnly: false);
        var runningFates = 0;
        foreach (var fate in fates)
        {
            if (fate.State == FFXIVClientStructs.FFXIV.Client.Game.Fate.FateState.Running)
            {
                runningFates++;
            }
        }

        var zone = CriticalEngagementScanner.DetectZone(Plugin.ClientState.TerritoryType);
        var engagements = this.plugin.Engagements.Scan(zone);

        ImGui.TextUnformatted($"FATE：{runningFates} 个进行中 / 共 {fates.Count} 个");
        ImGui.TextUnformatted($"紧急遭遇战：{engagements.Count} 个活动条目");

        var navmeshText = this.plugin.Navmesh.IsAvailable ? "已就绪" : "不可用";
        ImGui.TextUnformatted("vnavmesh：");
        ImGui.SameLine();
        ImGui.TextColored(
            this.plugin.Navmesh.IsAvailable ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudRed,
            navmeshText);

        ImGui.SameLine();
        ImGui.TextUnformatted("  Lifestream：");
        ImGui.SameLine();
        ImGui.TextColored(
            this.plugin.Lifestream.IsAvailable ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey,
            this.plugin.Lifestream.IsAvailable ? "已就绪" : "不可用");

        // ---- FATE list
        ImGui.Spacing();
        ImGui.TextUnformatted("附近的 FATE");
        ImGui.Separator();

        if (fates.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "当前地图没有 FATE 数据。");
        }
        else
        {
            using var table = ImRaii.Table("##fate-table", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
            if (table)
            {
                ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("等级", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("进度", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("距离", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableHeadersRow();

                foreach (var fate in fates)
                {
                    var distance = Vector3.Distance(playerPosition, fate.Position);

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (fate.IsBonus)
                    {
                        ImGui.TextColored(ImGuiColors.ParsedGold, fate.Name);
                    }
                    else
                    {
                        ImGui.TextUnformatted(fate.Name);
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(fate.Level.ToString());

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{fate.ProgressPercent}%");

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{distance:F0} m");
                }
            }
        }

        // ---- CE list
        ImGui.Spacing();
        ImGui.TextUnformatted("紧急遭遇战");
        ImGui.Separator();

        if (engagements.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "当前没有进行中的紧急遭遇战。");
        }
        else
        {
            foreach (var ce in engagements)
            {
                var stateText = ce.State switch
                {
                    FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEventState.Register => "报名中",
                    FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEventState.Warmup => "准备中",
                    FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.DynamicEventState.Battle => "战斗中",
                    _ => "未知",
                };

                ImGui.BulletText($"{ce.Name} — {stateText}（剩余 {ce.SecondsLeft}s）");
            }
        }

        // ---- travel button
        ImGui.Spacing();
        ImGui.Separator();
        if (ImGui.Button("手动前往最近的 FATE（一次性）"))
        {
            this.TravelToNearestOneShot(playerPosition);
        }

        ImGui.SameLine();
        if (ImGui.Button("停止寻路"))
        {
            this.plugin.Navmesh.Stop();
            this.plugin.Activity.Add("已手动停止寻路。");
        }
    }

    /// <summary>
    /// A one-off "go there now" that does not require turning the automation on.
    /// Useful for testing a route without committing to the whole loop.
    /// </summary>
    private void TravelToNearestOneShot(Vector3 playerPosition)
    {
        var fate = Automation.TargetSelector.SelectFate(
            this.plugin.Fates.Scan(playerPosition, runningOnly: true),
            playerPosition,
            this.config);

        if (fate is null)
        {
            this.plugin.Activity.Warning("没有符合条件的 FATE。");
            return;
        }

        if (!this.plugin.Navmesh.MoveCloseTo(fate.Position, 8f))
        {
            this.plugin.Activity.Error("寻路指令下发失败，请确认 vnavmesh 已就绪。");
            return;
        }

        this.plugin.Activity.Success($"手动前往 {fate.Name}。");
    }

    private void DrawDropTab()
    {
        ImGui.TextUnformatted("掉落提醒的物品");
        ImGui.Separator();

        ImGui.Checkbox("启用掉落提醒", ref this.config.DropAlertEnabled);
        ImGui.Checkbox("聊天栏提示", ref this.config.DropAlertChat);
        ImGui.Checkbox("音效提示", ref this.config.DropAlertSound);

        if (this.config.DropAlertSound)
        {
            ImGui.SetNextItemWidth(120);
            ImGui.InputInt("音效 ID", ref this.config.DropAlertSoundId);
            ImGui.SameLine();
            if (ImGui.Button("试听"))
            {
                try
                {
                    FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlayChatSoundEffect((uint)this.config.DropAlertSoundId);
                }
                catch
                {
                    // Ignore: audio failures are never worth interrupting the user.
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("添加默认提醒物品（德米亚特玛 / 灵魂碎片 等）"))
        {
            var defaults = this.plugin.Catalog.ResolveDefaultAlertItems();
            var added = 0;
            foreach (var id in defaults)
            {
                if (!this.config.DropAlertItemIds.Contains(id))
                {
                    this.config.DropAlertItemIds.Add(id);
                    added++;
                }
            }

            this.config.Save();
            this.plugin.Activity.Success($"已添加 {added} 个默认提醒物品（共匹配 {defaults.Count} 个）。");
        }

        ImGui.Spacing();
        if (this.config.DropAlertItemIds.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "尚未添加任何物品。");
            return;
        }

        if (ImGui.Button("清空列表"))
        {
            this.config.DropAlertItemIds.Clear();
            this.config.Save();
        }

        using var table = ImRaii.Table("##drop-table", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (table)
        {
            ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("ID", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 50);
            ImGui.TableHeadersRow();

            uint? remove = null;
            foreach (var id in this.config.DropAlertItemIds)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(this.plugin.Catalog.GetItemName(id));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(id.ToString());

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"删除##{id}"))
                {
                    remove = id;
                }
            }

            if (remove.HasValue)
            {
                this.config.DropAlertItemIds.Remove(remove.Value);
                this.config.Save();
            }
        }
    }

    private void DrawLogTab()
    {
        if (ImGui.Button("清空日志"))
        {
            this.plugin.Activity.Clear();
        }

        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"共 {this.plugin.Activity.Snapshot().Count} 条");

        ImGui.Separator();

        using var child = ImRaii.Child("##log-scroll", new Vector2(0, 0), false);
        if (!child)
        {
            return;
        }

        foreach (var entry in this.plugin.Activity.Snapshot())
        {
            var color = entry.Level switch
            {
                ActivityLog.Level.Success => ImGuiColors.ParsedGreen,
                ActivityLog.Level.Warning => ImGuiColors.ParsedGold,
                ActivityLog.Level.Error => ImGuiColors.DalamudRed,
                _ => ImGuiColors.DalamudWhite,
            };

            ImGui.TextColored(ImGuiColors.DalamudGrey, entry.Timestamp.ToString("HH:mm:ss"));
            ImGui.SameLine();
            ImGui.TextColored(color, entry.Message);
        }
    }

    private void DrawAboutTab()
    {
        ImGui.TextUnformatted("Bozja / 新月岛 助手");
        ImGui.Separator();

        ImGui.TextWrapped(
            "面向国服（卫月）的特殊野外内容自动化助手，同时支持博兹雅战线／扎杜诺尔高原与新月岛。");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "本插件不自行实现任何职业循环：战斗部分只负责锁定目标与交战距离，" +
            "技能释放交给 RSR / WrathCombo 等专业插件。");

        ImGui.Spacing();
        ImGui.TextUnformatted("命令");
        ImGui.Separator();
        ImGui.BulletText("/bozja 或 /bzj — 打开本窗口");
        ImGui.BulletText("/bzj on — 开始自动循环");
        ImGui.BulletText("/bzj off — 停止自动循环");
        ImGui.BulletText("/bzj panic — 紧急停止");
        ImGui.BulletText("/bzj status — 在聊天栏显示当前状态");
        ImGui.BulletText("/bzj reload — 刷新游戏数据缓存");

        ImGui.Spacing();
        ImGui.TextUnformatted("运行统计");
        ImGui.Separator();
        ImGui.BulletText($"完成 FATE：{this.config.TotalFatesCompleted}");
        ImGui.BulletText($"参加 CE：{this.config.TotalCesJoined}");
        ImGui.BulletText($"掉落提醒：{this.config.TotalDropsAlerted}");
    }

    public void Dispose()
    {
    }
}
