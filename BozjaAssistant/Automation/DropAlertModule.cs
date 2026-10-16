using System;
using System.Collections.Generic;
using System.Linq;
using BozjaAssistant.Data;
using Dalamud.Game.Inventory;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace BozjaAssistant.Automation;

/// <summary>
/// Watches the inventory for newly acquired items and alerts on the ones the user
/// cares about — Demiatma (德米亚特玛), soul shards, and any custom ids.
///
/// Why poll the inventory instead of listening for a chat line: loot in Bozja and
/// Occult Crescent does not reliably produce a localised chat message we can match
/// against, and the item name differs per client language. Comparing container
/// contents before and after is language-independent and catches every source of
/// acquisition (loot, quest reward, vendor, currency).
///
/// The scan is a diff of snapshot -> current, so it only reports genuinely new
/// stacks, not items that were already there.
/// </summary>
public sealed class DropAlertModule : IDisposable
{
    private readonly Configuration config;
    private readonly ActivityLog activity;
    private readonly GameDataCatalog catalog;
    private readonly IGameInventory gameInventory;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    /// <summary>Item id -> total count, from the previous scan.</summary>
    private readonly Dictionary<uint, int> previousCounts = new();

    private bool primed;

    public DropAlertModule(
        Configuration config,
        ActivityLog activity,
        GameDataCatalog catalog,
        IGameInventory gameInventory,
        IChatGui chat,
        IPluginLog log)
    {
        this.config = config;
        this.activity = activity;
        this.catalog = catalog;
        this.gameInventory = gameInventory;
        this.chat = chat;
        this.log = log;
    }

    /// <summary>
    /// Re-read every inventory container and report items whose count increased.
    /// Call on a reasonable cadence (every tick is fine; the reads are cheap).
    /// </summary>
    public void Tick()
    {
        if (!this.config.DropAlertEnabled)
        {
            return;
        }

        Dictionary<uint, int> current;
        try
        {
            current = this.Snapshot();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "扫描背包失败");
            return;
        }

        // First pass only establishes the baseline, so logging in while already
        // carrying maps does not fire a wall of alerts.
        if (!this.primed)
        {
            this.previousCounts.Clear();
            foreach (var (id, count) in current)
            {
                this.previousCounts[id] = count;
            }

            this.primed = true;
            return;
        }

        foreach (var (itemId, count) in current)
        {
            var before = this.previousCounts.TryGetValue(itemId, out var prev) ? prev : 0;
            if (count <= before)
            {
                continue;
            }

            this.previousCounts[itemId] = count;

            var gained = count - before;
            if (this.config.DropAlertItemIds.Contains(itemId))
            {
                this.Alert(itemId, gained);
            }
        }

        // Forget items that vanished entirely, so re-acquiring one later alerts again.
        foreach (var id in this.previousCounts.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            this.previousCounts.Remove(id);
        }
    }

    /// <summary>Drop the baseline so the next tick re-primes (used after a zone change).</summary>
    public void Reset()
    {
        this.primed = false;
        this.previousCounts.Clear();
    }

    private void Alert(uint itemId, int gained)
    {
        var name = this.catalog.GetItemName(itemId);
        this.config.TotalDropsAlerted++;
        this.activity.Success($"获得 {name} ×{gained}");

        if (this.config.DropAlertChat)
        {
            this.chat.Print($"[Bozja] 获得 {name} ×{gained}");
        }

        if (this.config.DropAlertSound)
        {
            try
            {
                // Verified in FFXIVClientStructs Client/UI/UIGlobals.cs:
                //   public static void PlayChatSoundEffect(uint effectId)
                // which forwards to PlaySoundEffect(effectId + 36). This is the
                // same path the game uses for chat notification sounds, so the id
                // here is a *chat* sound id (the default 9 is the tell sound).
                UIGlobals.PlayChatSoundEffect((uint)this.config.DropAlertSoundId);
            }
            catch (Exception ex)
            {
                this.log.Debug(ex, "播放掉落提示音失败");
            }
        }
    }

    /// <summary>Total count per item id across every readable container.</summary>
    private Dictionary<uint, int> Snapshot()
    {
        var totals = new Dictionary<uint, int>();

        foreach (var container in TrackedContainers)
        {
            foreach (var item in this.gameInventory.GetInventoryItems(container))
            {
                if (item.ItemId == 0)
                {
                    continue;
                }

                var id = (uint)item.ItemId;
                var quantity = Math.Max(1, (int)item.Quantity);
                totals[id] = totals.TryGetValue(id, out var have) ? have + quantity : quantity;
            }
        }

        return totals;
    }

    /// <summary>
    /// Containers worth watching. Player bags and the saddlebag cover every way
    /// loot reaches the player; retainers and the glamour dresser are excluded
    /// because moving an item there should not read as acquiring it.
    /// </summary>
    private static readonly GameInventoryType[] TrackedContainers =
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
        GameInventoryType.SaddleBag1,
        GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1,
        GameInventoryType.PremiumSaddleBag2,
    };

    public void Dispose()
    {
    }
}
