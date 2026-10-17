using System;
using System.Numerics;
using BozjaAssistant.Data;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace BozjaAssistant.Windows;

/// <summary>
/// The settings window. Every module from the original bozjalone feature set has a
/// section here, and every switch is persisted on change.
///
/// Sections are collapsible and collapsed-by-default except the master switches,
/// because the common case is toggling one thing — not reading all of it.
/// </summary>
public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Configuration config;

    public ConfigWindow(Plugin plugin)
        : base("Bozja / 新月岛 助手 — 设置###BozjaAssistantConfig")
    {
        this.plugin = plugin;
        this.config = plugin.Configuration;

        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.Size = new Vector2(500, 560);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        this.SaveOnChange(() => ImGui.Checkbox("启用自动循环", ref this.config.AutomationEnabled));
        this.SaveOnChange(() => ImGui.Checkbox("仅在博兹雅 / 新月岛内运行", ref this.config.OnlyInSupportedZones));

        var showDtr = this.config.ShowDtrEntry;
        if (ImGui.Checkbox("在服务器信息栏显示状态", ref showDtr))
        {
            this.config.ShowDtrEntry = showDtr;
            this.config.Save();
            // The DTR entry is created/destroyed imperatively, so push the change.
            this.plugin.SyncDtrEntry();
        }

        ImGui.Separator();

        this.Section("FATE 模块", FontAwesomeIcon.Map, this.DrawFateSection);
        this.Section("紧急遭遇战（CE）模块", FontAwesomeIcon.Crosshairs, this.DrawCeSection);
        this.Section("自动战斗", FontAwesomeIcon.Khanda, this.DrawCombatSection);
        this.Section("通用自动化", FontAwesomeIcon.Robot, this.DrawGeneralSection);
        this.Section("紧急停止与统计", FontAwesomeIcon.Ban, this.DrawMiscSection);
    }

    private void Section(string label, FontAwesomeIcon icon, Action draw)
    {
        // ImRaii.Header with the CollapsingHeader flag is Dalamud's spelling of
        // ImGui.CollapsingHeader; there is no ImRaii.CollapsingHeader.
        using var header = ImRaii.Header(
            $"{icon.ToIconString()} {label}###section-{label}",
            ImGuiTreeNodeFlags.CollapsingHeader);
        if (!header.Success)
        {
            return;
        }

        using var indent = ImRaii.PushIndent(1, true);
        draw();
        ImGui.Spacing();
    }

    private void DrawFateSection()
    {
        this.SaveOnChange(() => ImGui.Checkbox("自动前往 FATE", ref this.config.FateEnabled));

        if (!this.config.FateEnabled)
        {
            return;
        }

        this.SaveOnChange(() => ImGui.Checkbox("优先加成 FATE", ref this.config.FatePreferBonus));

        ImGui.SetNextItemWidth(200);
        var priority = (int)this.config.FatePriority;
        if (ImGui.Combo("选择策略", ref priority,
                new[] { "最近", "进度最低", "剩余时间最少", "等级最高" }, 4))
        {
            this.config.FatePriority = (FatePriorityMode)priority;
            this.config.Save();
        }

        ImGui.SetNextItemWidth(200);
        this.SaveOnChange(() => ImGui.SliderFloat("最大距离（米，0 = 不限）", ref this.config.FateMaxDistance, 0f, 2000f));

        ImGui.SetNextItemWidth(200);
        this.SaveOnChange(() => ImGui.SliderInt("进度高于此值则跳过（%）", ref this.config.FateSkipProgressAbove, 0, 100));

        ImGui.SetNextItemWidth(200);
        this.SaveOnChange(() => ImGui.SliderInt("剩余时间低于此值则跳过（秒）", ref this.config.FateSkipSecondsLeftBelow, 0, 300));

        ImGui.SetNextItemWidth(200);
        this.SaveOnChange(() => ImGui.SliderInt("重新扫描间隔（秒）", ref this.config.FateRescanSeconds, 1, 30));
    }

    private void DrawCeSection()
    {
        this.SaveOnChange(() => ImGui.Checkbox("自动前往紧急遭遇战", ref this.config.CeEnabled));

        if (!this.config.CeEnabled)
        {
            return;
        }

        this.SaveOnChange(() => ImGui.Checkbox("报名阶段即前往", ref this.config.CeJoinOnRegister));
        this.SaveOnChange(() => ImGui.Checkbox("准备阶段继续前往", ref this.config.CeJoinOnWarmup));
        this.SaveOnChange(() => ImGui.Checkbox("优先于 FATE（遭遇战限时）", ref this.config.CeOverridesFate));

        ImGui.SetNextItemWidth(200);
        this.SaveOnChange(() => ImGui.SliderFloat("最大距离（米，0 = 不限）", ref this.config.CeMaxDistance, 0f, 3000f));

        ImGui.Spacing();
        ImGui.TextUnformatted("黑名单（名称包含即跳过，每行一条）");
        ImGui.Separator();

        var blacklistText = string.Join("\n", this.config.CeBlacklist);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextMultiline("##ce-blacklist", ref blacklistText, 4096, new Vector2(0, 70)))
        {
            this.config.CeBlacklist.Clear();
            foreach (var line in blacklistText.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    this.config.CeBlacklist.Add(trimmed);
                }
            }

            this.config.Save();
        }
    }

    private void DrawCombatSection()
    {
        this.SaveOnChange(() => ImGui.Checkbox("启用自动战斗", ref this.config.CombatEnabled));

        if (!this.config.CombatEnabled)
        {
            return;
        }

        this.SaveOnChange(() => ImGui.Checkbox("自动锁定最近敌人", ref this.config.AutoTarget));

        ImGui.SetNextItemWidth(200);
        this.SaveOnChange(() => ImGui.SliderFloat("交战距离（米）", ref this.config.EngageRange, 5f, 50f));

        ImGui.Spacing();
        this.SaveOnChange(() => ImGui.Checkbox("联动外部循环插件", ref this.config.ExternalCombatAi));

        if (!this.config.ExternalCombatAi)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "本插件不会自己释放技能。");
            return;
        }

        ImGui.SetNextItemWidth(200);
        var provider = (int)this.config.ExternalAi;
        if (ImGui.Combo("选择插件", ref provider,
                new[] { "不联动", "Rotation Solver Reborn", "WrathCombo" }, 3))
        {
            this.config.ExternalAi = (ExternalAiProvider)provider;
            this.config.Save();
        }

        var available = this.plugin.CombatAi.IsAvailable(this.config.ExternalAi);
        ImGui.TextColored(
            available ? ImGuiColors.ParsedGreen : ImGuiColors.DalamudGrey,
            available ? "已检测到该插件。" : "未检测到该插件（未安装或未加载）。");
    }

    private void DrawGeneralSection()
    {
        this.SaveOnChange(() => ImGui.Checkbox("死亡后自动复活", ref this.config.AutoRevive));

        if (this.config.AutoRevive)
        {
            ImGui.SetNextItemWidth(200);
            this.SaveOnChange(() => ImGui.SliderInt("复活延迟（秒）", ref this.config.AutoReviveDelaySeconds, 0, 30));
        }

        this.SaveOnChange(() => ImGui.Checkbox("复活后自动返回目标", ref this.config.AutoReturnAfterRevive));
        this.SaveOnChange(() => ImGui.Checkbox("自动上坐骑", ref this.config.AutoMount));

        if (this.config.AutoMount)
        {
            ImGui.SetNextItemWidth(200);
            this.SaveOnChange(() => ImGui.SliderFloat("上坐骑距离阈值（米）", ref this.config.AutoMountDistance, 10f, 300f));
        }

        ImGui.Spacing();
        this.SaveOnChange(() => ImGui.Checkbox("使用以太水晶（以太之光）跨层移动", ref this.config.AethernetRouting));

        if (this.config.AethernetRouting)
        {
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "新月岛的上/中/下层互不相通，只能靠内部以太水晶传送。");
            ImGui.TextColored(
                ImGuiColors.DalamudGrey,
                "开启后，目标在别层时会先走到本层水晶再传送（需要 Lifestream）。");
        }
        else
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "已关闭：别层的目标将无法到达。");
        }
    }

    private void DrawMiscSection()
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey, $"紧急停止热键：F{this.config.PanicHotkey - 111}（默认 F12）");

        if (this.config.EmergencyStop)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, "紧急停止生效中。");
            if (ImGui.Button("解除紧急停止"))
            {
                this.config.EmergencyStop = false;
                this.config.Save();
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"完成 FATE：{this.config.TotalFatesCompleted}");
        ImGui.TextUnformatted($"参加 CE：{this.config.TotalCesJoined}");
        ImGui.TextUnformatted($"掉落提醒：{this.config.TotalDropsAlerted}");

        ImGui.Spacing();
        if (ImGui.Button("重置统计"))
        {
            this.config.TotalFatesCompleted = 0;
            this.config.TotalCesJoined = 0;
            this.config.TotalDropsAlerted = 0;
            this.config.Save();
        }
    }

    /// <summary>
    /// Run a widget and persist the config when it reports a change. Keeping the
    /// save next to the widget is what makes it hard to add a setting and forget
    /// to persist it.
    /// </summary>
    private void SaveOnChange(Func<bool> widget)
    {
        if (widget())
        {
            this.config.Save();
        }
    }

    public void Dispose()
    {
    }
}
