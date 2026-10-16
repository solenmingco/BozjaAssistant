using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace BozjaAssistant.Automation;

/// <summary>
/// Thin wrapper over the IPC surface of automated-rotation plugins, so the user
/// can keep using whichever rotation AI they already trust instead of this plugin
/// reimplementing job rotations (which would be both enormous and worse).
///
/// Three providers are supported, each behind its own guarded call:
///
///  RotationSolverReborn (InternalName "RotationSolver")
///      The `/rsr` command surface, driven through the plugin's own command
///      handler rather than a dedicated IPC channel, because RSR exposes its
///      state control through commands.
///
///  WrathCombo (InternalName "WrathCombo")
///      WrathCombo.API.* channels, used to toggle auto-rotation on and off.
///
///  BossMod is deliberately *not* driven here: it is a fight-mechanics helper, not
///  a rotation solver, so toggling it would change the user's UI rather than make
///  the character fight.
///
/// Every call is defensive — the plugin may be absent, and an IPC call to an
/// unregistered channel throws.
/// </summary>
public sealed class CombatAiIpc : IDisposable
{
    private readonly IPluginLog log;

    // ---- Rotation Solver Reborn ------------------------------------------
    private readonly ICallGateSubscriber<string, object?> rsrCommand;

    // ---- WrathCombo ------------------------------------------------------
    private readonly ICallGateSubscriber<bool> wrathGetAutoRotation;
    private readonly ICallGateSubscriber<bool, object?> wrathSetAutoRotation;

    public CombatAiIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;

        this.rsrCommand = pluginInterface.GetIpcSubscriber<string, object?>("RotationSolver.ExecuteCommand");

        this.wrathGetAutoRotation = pluginInterface.GetIpcSubscriber<bool>("WrathCombo.API.GetAutoRotationState");
        this.wrathSetAutoRotation = pluginInterface.GetIpcSubscriber<bool, object?>("WrathCombo.API.SetAutoRotationState");
    }

    /// <summary>Whether a given provider currently looks reachable.</summary>
    public bool IsAvailable(ExternalAiProvider provider)
    {
        try
        {
            switch (provider)
            {
                case ExternalAiProvider.RotationSolverReborn:
                    // No dedicated status channel; a harmless no-op command is the
                    // only reachability probe available.
                    return true;

                case ExternalAiProvider.WrathCombo:
                    _ = this.wrathGetAutoRotation.InvokeFunc();
                    return true;

                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Turn automated rotation on for the configured provider.</summary>
    public void Enable(ExternalAiProvider provider)
    {
        switch (provider)
        {
            case ExternalAiProvider.RotationSolverReborn:
                this.RunRsr("on");
                break;

            case ExternalAiProvider.WrathCombo:
                this.SetWrath(true);
                break;
        }
    }

    /// <summary>Turn automated rotation off for the configured provider.</summary>
    public void Disable(ExternalAiProvider provider)
    {
        switch (provider)
        {
            case ExternalAiProvider.RotationSolverReborn:
                this.RunRsr("off");
                break;

            case ExternalAiProvider.WrathCombo:
                this.SetWrath(false);
                break;
        }
    }

    private void RunRsr(string args)
    {
        try
        {
            this.rsrCommand.InvokeAction(args);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "调用 RotationSolver 命令失败：{Args}", args);
        }
    }

    private void SetWrath(bool enabled)
    {
        try
        {
            this.wrathSetAutoRotation.InvokeAction(enabled);
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "设置 WrathCombo 自动循环失败");
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// The combat module: keeps a hostile target selected and in range, and hands the
/// actual ability usage to an external rotation AI when one is configured.
///
/// This plugin deliberately does not cast anything itself. Rotations are
/// job-specific, patch-sensitive and a solved problem done better by dedicated
/// plugins; the value this module adds is target acquisition and engagement
/// distance, which those plugins do not do while the player is travelling between
/// FATEs.
/// </summary>
public sealed class CombatModule : IDisposable
{
    private readonly Configuration config;
    private readonly ActivityLog activity;
    private readonly CombatAiIpc ai;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    private bool aiEnabledByUs;

    public CombatModule(
        Configuration config,
        ActivityLog activity,
        CombatAiIpc ai,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IClientState clientState,
        IPluginLog log)
    {
        this.config = config;
        this.activity = activity;
        this.ai = ai;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.clientState = clientState;
        this.log = log;
    }

    /// <summary>Returns the current target, selecting one if needed.</summary>
    public void Tick(Vector3 playerPosition)
    {
        if (!this.config.CombatEnabled || this.config.EmergencyStop)
        {
            this.DisableAi();
            return;
        }

        var player = this.objectTable.LocalPlayer;
        if (player is null || player.IsDead)
        {
            this.DisableAi();
            return;
        }

        if (this.config.AutoTarget)
        {
            this.EnsureTarget(playerPosition);
        }

        this.SyncAi();
    }

    /// <summary>
    /// Ensure something hostile and in range is selected. An existing valid target
    /// is always kept — retargeting every tick would thrash the rotation AI.
    /// </summary>
    private void EnsureTarget(Vector3 playerPosition)
    {
        var current = this.targetManager.Target;
        if (current is not null && this.IsValidTarget(current, playerPosition))
        {
            return;
        }

        IGameObject? best = null;
        var bestDistance = float.MaxValue;

        foreach (var obj in this.objectTable)
        {
            if (!this.IsValidTarget(obj, playerPosition))
            {
                continue;
            }

            var distance = Vector3.Distance(playerPosition, obj.Position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = obj;
            }
        }

        if (best is not null)
        {
            this.targetManager.Target = best;
        }
    }

    private bool IsValidTarget(IGameObject obj, Vector3 playerPosition)
    {
        // BattleNpc covers FATE/CE enemies; EventObj covers the scripted objects
        // some encounters use as the objective.
        if (obj.ObjectKind is not (ObjectKind.BattleNpc or ObjectKind.EventObj))
        {
            return false;
        }

        if (!obj.IsTargetable || obj.IsDead)
        {
            return false;
        }

        return Vector3.Distance(playerPosition, obj.Position) <= this.config.EngageRange;
    }

    private void SyncAi()
    {
        if (!this.config.ExternalCombatAi || this.config.ExternalAi == ExternalAiProvider.None)
        {
            this.DisableAi();
            return;
        }

        if (this.aiEnabledByUs)
        {
            return;
        }

        this.ai.Enable(this.config.ExternalAi);
        this.aiEnabledByUs = true;
        this.activity.Add($"已启用外部循环插件：{this.config.ExternalAi}");
    }

    private void DisableAi()
    {
        if (!this.aiEnabledByUs)
        {
            return;
        }

        this.ai.Disable(this.config.ExternalAi);
        this.aiEnabledByUs = false;
    }

    public void Dispose()
    {
        // Never leave the user's rotation plugin switched on because we crashed.
        this.DisableAi();
    }
}
