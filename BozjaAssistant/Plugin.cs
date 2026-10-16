using System;
using System.Numerics;
using BozjaAssistant.Automation;
using BozjaAssistant.Data;
using BozjaAssistant.Windows;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace BozjaAssistant;

/// <summary>
/// Plugin entry point. Owns the services, the automation controller and the UI,
/// and wires the chat commands.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/bozja";
    private const string CommandAlias = "/bzj";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Configuration { get; }

    public readonly WindowSystem WindowSystem = new("BozjaAssistant");
    private MainWindow MainWindow { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    /// <summary>Read-only catalog of game data.</summary>
    public GameDataCatalog Catalog { get; }

    /// <summary>Snapshotter for the live FATE table.</summary>
    public FateScanner Fates { get; }

    /// <summary>Snapshotter for the Critical Engagement table.</summary>
    public CriticalEngagementScanner Engagements { get; }

    /// <summary>vnavmesh IPC, for walking.</summary>
    public NavmeshIpc Navmesh { get; }

    /// <summary>Lifestream IPC, for teleporting.</summary>
    public LifestreamIpc Lifestream { get; }

    /// <summary>External rotation-AI IPC.</summary>
    public CombatAiIpc CombatAi { get; }

    /// <summary>Drop alerts.</summary>
    public DropAlertModule DropAlerts { get; }

    /// <summary>Combat targeting.</summary>
    public CombatModule Combat { get; }

    /// <summary>The automation state machine.</summary>
    public AutomationController Automation { get; }

    /// <summary>Rolling in-memory log shown in the UI.</summary>
    public ActivityLog Activity { get; } = new();

    private IDtrBarEntry? dtrEntry;

    public Plugin()
    {
        this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.Catalog = new GameDataCatalog(DataManager, Log);
        this.Fates = new FateScanner(Log);
        this.Engagements = new CriticalEngagementScanner(Log);
        this.Navmesh = new NavmeshIpc(PluginInterface, Log);
        this.Lifestream = new LifestreamIpc(PluginInterface, Log);
        this.CombatAi = new CombatAiIpc(PluginInterface, Log);
        this.DropAlerts = new DropAlertModule(
            this.Configuration,
            this.Activity,
            this.Catalog,
            GameInventory,
            Chat,
            Log);
        this.Combat = new CombatModule(
            this.Configuration,
            this.Activity,
            this.CombatAi,
            ObjectTable,
            TargetManager,
            ClientState,
            Log);

        this.Automation = new AutomationController(this);

        this.ConfigWindow = new ConfigWindow(this);
        this.MainWindow = new MainWindow(this);

        this.WindowSystem.AddWindow(this.ConfigWindow);
        this.WindowSystem.AddWindow(this.MainWindow);

        PluginInterface.UiBuilder.Draw += this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += this.ToggleMainUi;

        CommandManager.AddHandler(CommandName, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "打开 Bozja / 新月岛助手主窗口。",
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Bozja / 新月岛助手（/bozja 的别名）。",
        });

        if (this.Configuration.ShowDtrEntry)
        {
            this.dtrEntry = DtrBar.Get("BozjaAssistant");
            this.dtrEntry.Shown = true;
        }

        this.Activity.Add("插件已加载。");
        Log.Information("Bozja / 新月岛助手已加载。");
    }

    public void Dispose()
    {
        this.Automation.Dispose();
        this.Combat.Dispose();
        this.DropAlerts.Dispose();
        this.CombatAi.Dispose();
        this.Navmesh.Dispose();
        this.Lifestream.Dispose();

        PluginInterface.UiBuilder.Draw -= this.WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= this.ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= this.ToggleMainUi;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);

        if (this.dtrEntry is not null)
        {
            this.dtrEntry.Remove();
            this.dtrEntry = null;
        }

        this.WindowSystem.RemoveAllWindows();
        this.ConfigWindow.Dispose();
        this.MainWindow.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "on":
            case "start":
                this.Automation.Start();
                break;

            case "off":
            case "stop":
                this.Automation.Stop();
                break;

            case "panic":
                this.Automation.EmergencyStop();
                break;

            case "reload":
                this.Catalog.Invalidate();
                this.Activity.Success("游戏数据缓存已刷新。");
                break;

            case "clear":
                this.Activity.Clear();
                Chat.Print("[Bozja] 日志已清空。");
                break;

            case "status":
                Chat.Print($"[Bozja] {this.Automation.StatusText}");
                break;

            case "":
            case "ui":
                this.MainWindow.Toggle();
                break;

            default:
                Chat.Print($"[Bozja] 未知参数 “{args}”。可用：on / off / panic / status / reload / clear / ui");
                break;
        }
    }

    public void ToggleConfigUi() => this.ConfigWindow.Toggle();

    public void ToggleMainUi() => this.MainWindow.Toggle();

    /// <summary>Refresh the server-info-bar text. Called from the automation tick.</summary>
    internal void UpdateDtr(string text)
    {
        if (this.dtrEntry is null)
        {
            return;
        }

        this.dtrEntry.Text = text;
        this.dtrEntry.Shown = this.Configuration.ShowDtrEntry;
    }

    /// <summary>Recreate the DTR entry when the user toggles it at runtime.</summary>
    internal void SyncDtrEntry()
    {
        if (this.Configuration.ShowDtrEntry && this.dtrEntry is null)
        {
            this.dtrEntry = DtrBar.Get("BozjaAssistant");
        }

        if (this.dtrEntry is not null)
        {
            this.dtrEntry.Shown = this.Configuration.ShowDtrEntry;
        }
    }
}
