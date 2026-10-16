using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;

namespace BozjaAssistant.Data;

/// <summary>
/// Read-only catalog of game data (item names, etc), with a small cache.
///
/// Everything here is read from the game's own sheets via <see cref="IDataManager"/>,
/// never hardcoded: the Chinese client's row ids and names can differ from the
/// global client, so hardcoding would silently break for the user this targets.
/// </summary>
public sealed class GameDataCatalog
{
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, string> itemNameCache = new();

    public GameDataCatalog(IDataManager dataManager, IPluginLog log)
    {
        this.dataManager = dataManager;
        this.log = log;
    }

    /// <summary>Display name for an item id, or a placeholder when unknown.</summary>
    public string GetItemName(uint itemId)
    {
        if (this.itemNameCache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var name = $"物品#{itemId}";
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (sheet is not null && sheet.TryGetRow(itemId, out var row))
            {
                var resolved = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    name = resolved;
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "读取物品名称失败：{Id}", itemId);
        }

        this.itemNameCache[itemId] = name;
        return name;
    }

    /// <summary>
    /// Item ids whose acquisition should raise an alert by default.
    ///
    /// These are resolved *by name* against the item sheet at runtime rather than
    /// hardcoded as row ids, because the ids differ between the global and the
    /// Chinese client. Names that resolve to nothing are simply skipped.
    /// </summary>
    public List<uint> ResolveDefaultAlertItems()
    {
        // 德米亚特玛 (Demiatma) — the Occult Crescent upgrade material family, and
        // the Bozja soul-shard / memory family that bozjalone alerts on.
        string[] wanted =
        {
            "德米亚特玛", "Demiatma",
            "灵魂碎片", "Soul Shard",
            "战果记录", "Bozjan Cluster",
            "失落记忆", "Lost Memory",
        };

        var found = new List<uint>();
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (sheet is null)
            {
                return found;
            }

            foreach (var row in sheet)
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (wanted.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(row.RowId);
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "解析默认掉落提醒物品失败");
        }

        return found;
    }

    /// <summary>Item ids whose name matches the given search text, for the UI picker.</summary>
    public List<(uint Id, string Name)> SearchItems(string query, int limit = 50)
    {
        var results = new List<(uint, string)>();
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            return results;
        }

        try
        {
            var sheet = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            if (sheet is null)
            {
                return results;
            }

            foreach (var row in sheet)
            {
                if (results.Count >= limit)
                {
                    break;
                }

                var name = row.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name)
                    && name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((row.RowId, name));
                }
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "搜索物品失败：{Query}", query);
        }

        return results;
    }

    /// <summary>Clear every cache. Called when the user hits /bzj reload.</summary>
    public void Invalidate() => this.itemNameCache.Clear();
}
