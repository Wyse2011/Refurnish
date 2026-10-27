using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Refurnish.Inventory;

namespace Refurnish.Windows;

public sealed partial class MainWindow
{
    private List<InventoryRow>? invRows;
    private List<CharacterCache> invCharacters = new();
    private int invVersion = -1;
    private DateTime invBuiltUtc;
    private string invSearch = string.Empty;
    private string invCharacter = "All characters";
    private string invType = "All places";
    private bool invCombine = true;
    private string? invMessage;

    private void DrawInventoryTab()
    {
        // Rebuild when anything was seen or saved (and at most every few seconds otherwise).
        if (invRows == null || invVersion != plugin.Cache.Version || DateTime.UtcNow - invBuiltUtc > TimeSpan.FromSeconds(10))
        {
            invCharacters = plugin.Cache.All();
            invRows = HousingInventory.Build(invCharacters);
            invVersion = plugin.Cache.Version;
            invBuiltUtc = DateTime.UtcNow;
        }
        var rows = invRows;

        ImGui.TextWrapped("Every housing furnishing Refurnish has seen, across all your characters. It updates as you play: log in to each "
                          + "character, open each retainer at a summoning bell, view each FC chest compartment, and visit each room "
                          + "(with its storeroom tab open) once. Shared places (FC chest, house storerooms) are counted once.");
        ImGui.Spacing();

        var distinct = rows.Select(r => (r.ItemId, r.Stain)).Distinct().Count();
        ImGui.TextColored(Good, $"{rows.Sum(r => r.Quantity)} furnishings ({distinct} different) across {invCharacters.Count} character(s).");

        DrawInventoryCoverage();
        ImGui.Spacing();

        // ---- Filters ----
        ImGui.SetNextItemWidth(220);
        ImGui.InputTextWithHint("##invSearch", "Search items...", ref invSearch, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        Combo("##invChar", ref invCharacter, new[] { "All characters" }.Concat(rows.Select(r => r.Character).Distinct().OrderBy(c => c)));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140);
        Combo("##invType", ref invType, new[] { "All places", "Bags", "Saddlebag", "Retainer", "FC chest", "Storeroom", "Placed" });
        ImGui.SameLine();
        ImGui.Checkbox("Combine places##invCombine", ref invCombine);

        var filtered = rows.Where(r =>
                (invCharacter == "All characters" || r.Character == invCharacter)
                && (invType == "All places" || r.Type == invType)
                && (invSearch.Length == 0 || ItemName(r.ItemId).Contains(invSearch, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // ---- Export ----
        if (ImGui.Button("Export to spreadsheet (CSV)##invExport")) invMessage = Export(rows, "all");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Everything, as a CSV file that opens in Excel or Google Sheets.");
        ImGui.SameLine();
        ImGui.BeginDisabled(filtered.Count == rows.Count);
        if (ImGui.Button("Export what's shown##invExportShown")) invMessage = Export(filtered, "filtered");
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Copy as CSV##invCopy"))
        {
            ImGui.SetClipboardText(HousingInventory.ToCsv(filtered, ItemName, s => stains!.Name(s)));
            invMessage = "Copied. Paste into a spreadsheet.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Open export folder##invFolder")) OpenFolder(ExportFolder());
        if (invMessage != null) ImGui.TextDisabled(invMessage);

        ImGui.Spacing();
        if (filtered.Count == 0)
        {
            ImGui.TextDisabled(rows.Count == 0 ? "Nothing seen yet. Open your inventory, retainers and storerooms." : "Nothing matches.");
            return;
        }

        if (invCombine) DrawInventoryCombined(filtered);
        else DrawInventoryDetailed(filtered);
    }

    /// <summary>Which characters are known, and how many of their retainers have been opened.</summary>
    private void DrawInventoryCoverage()
    {
        if (!ImGui.TreeNodeEx($"Characters ({invCharacters.Count})###invChars")) return;
        foreach (var c in invCharacters.OrderBy(c => c.CharacterName))
        {
            var seen = c.Containers.Keys.Count(k => k.StartsWith("retainer:"));
            var total = c.Retainers.Count;
            var last = c.Containers.Values.Select(k => k.LastSeenUtc).DefaultIfEmpty().Max();
            var line = $"{(string.IsNullOrEmpty(c.CharacterName) ? "(unnamed)" : c.CharacterName)}: "
                       + (total == 0 ? "retainer list not loaded yet (use a summoning bell)" : $"{seen} of {total} retainers opened")
                       + (last == default ? "" : $", last updated {Ago(last)}");
            if (total > 0 && seen < total) ImGui.TextColored(Warn, line);
            else ImGui.TextUnformatted(line);
        }
        ImGui.TreePop();
    }

    private void DrawInventoryCombined(List<InventoryRow> rows)
    {
        var groups = rows.GroupBy(r => (r.ItemId, r.Stain))
            .Select(g => (g.Key.ItemId, g.Key.Stain, Total: g.Sum(r => r.Quantity), Rows: g.OrderByDescending(r => r.Quantity).ToList()))
            .OrderBy(g => ItemName(g.ItemId))
            .ToList();

        if (!ImGui.BeginTable("##invCombined", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Dye", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 50f);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 5f);
        ImGui.TableHeadersRow();

        foreach (var g in groups)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ItemName(g.ItemId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stains!.Name(g.Stain));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(g.Total.ToString());
            ImGui.TableNextColumn();
            var where = string.Join(", ", g.Rows.Select(r => $"{Short(r)} ×{r.Quantity}"));
            ImGui.TextUnformatted(where.Length > 140 ? where[..140] + "..." : where);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("\n", g.Rows.Select(r => $"{r.Quantity} × {r.Character}: {r.Location} (checked {Ago(r.LastSeenUtc)})")));
        }
        ImGui.EndTable();
    }

    private void DrawInventoryDetailed(List<InventoryRow> rows)
    {
        if (!ImGui.BeginTable("##invDetailed", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("Dye", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 40f);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Location", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Checked", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableHeadersRow();

        foreach (var r in rows.OrderBy(r => ItemName(r.ItemId)).ThenBy(r => r.Character).ThenBy(r => r.Location))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ItemName(r.ItemId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stains!.Name(r.Stain));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Character);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Location);
            ImGui.TableNextColumn();
            var stale = DateTime.UtcNow - r.LastSeenUtc > TimeSpan.FromDays(plugin.Configuration.StaleAfterDays);
            if (stale) ImGui.TextColored(Warn, Ago(r.LastSeenUtc));
            else ImGui.TextDisabled(Ago(r.LastSeenUtc));
        }
        ImGui.EndTable();
    }

    private static string Short(InventoryRow r) => r.Shared ? r.Location : $"{r.Character}: {r.Location}";

    private static void Combo(string id, ref string value, IEnumerable<string> options)
    {
        if (!ImGui.BeginCombo(id, value)) return;
        foreach (var option in options)
            if (ImGui.Selectable(option, option == value)) value = option;
        ImGui.EndCombo();
    }

    private static string ExportFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Refurnish");

    private string Export(List<InventoryRow> rows, string which)
    {
        try
        {
            var folder = ExportFolder();
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Housing inventory ({which}) {DateTime.Now:yyyy-MM-dd HHmm}.csv");
            // With a byte-order mark so Excel reads accented names correctly.
            File.WriteAllText(path, HousingInventory.ToCsv(rows, ItemName, s => stains!.Name(s)), new UTF8Encoding(true));
            return $"Saved {path}";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Export failed");
            return $"Couldn't save the file: {ex.Message}";
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Couldn't open the folder");
        }
    }
}
