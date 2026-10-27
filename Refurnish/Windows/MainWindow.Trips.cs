using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Refurnish.Inventory;
using Refurnish.Planning;

namespace Refurnish.Windows;

public sealed partial class MainWindow
{
    private TripPlan? trip;
    private int tripVersion = -1;
    private ulong tripHouse;
    private object? tripLayout;
    private DateTime tripBuiltUtc;
    private bool tripHideDone = true;

    private void DrawTripTab()
    {
        if (layout == null || selectedHouse == 0) return;
        var characters = plugin.Cache.All();
        var currentId = Plugin.PlayerState.ContentId;

        if (trip == null || tripVersion != plugin.Cache.Version || tripHouse != selectedHouse || !ReferenceEquals(tripLayout, layout)
            || DateTime.UtcNow - tripBuiltUtc > TimeSpan.FromSeconds(10))
        {
            var rooms = characters.SelectMany(c => c.Houses.Values).GroupBy(h => h.HouseId)
                .ToDictionary(g => g.Key, g => g.First().Label);
            trip = TripPlanner.Build(layout, characters, currentId, selectedHouse,
                id => rooms.TryGetValue(id, out var label) ? label : "another room");
            tripVersion = plugin.Cache.Version;
            tripHouse = selectedHouse;
            tripLayout = layout;
            tripBuiltUtc = DateTime.UtcNow;
        }

        ImGui.TextWrapped("Everything this layout still needs, as a list of stops across all your characters. Items on other "
                          + "characters go through the FC chest: that character deposits them, then you collect them. The list updates "
                          + "by itself as items move, so finished stops drop off.");
        ImGui.Spacing();

        var toFetch = trip.Stops.Where(s => !s.AlreadyThere).Sum(s => s.Items.Sum(i => i.Count));
        ImGui.TextColored(Good, $"{trip.AlreadyPlaced} already in place.");
        ImGui.SameLine();
        ImGui.TextUnformatted($"{trip.Stops.Where(s => s.AlreadyThere).Sum(s => s.Items.Sum(i => i.Count))} ready to place.");
        ImGui.SameLine();
        ImGui.TextColored(toFetch > 0 ? Warn : Good, $"{toFetch} to fetch.");
        if (trip.Missing.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Bad, $"{trip.Missing.Sum(m => m.Count)} not owned.");
        }

        foreach (var w in trip.Warnings) ImGui.TextColored(Warn, w);
        DrawTripCoverage(characters);

        if (ImGui.SmallButton("Copy trip list##trip"))
            ImGui.SetClipboardText(TripPlanner.ToText(trip, layout.FileName, ItemName, s => stains!.Name(s)));
        ImGui.SameLine();
        ImGui.Checkbox("Hide items already in the room's storeroom or your bags##tripHide", ref tripHideDone);
        ImGui.Spacing();

        var you = string.IsNullOrEmpty(Plugin.PlayerState.CharacterName) ? "You" : Plugin.PlayerState.CharacterName;
        foreach (var stop in trip.Stops)
        {
            if (tripHideDone && stop.AlreadyThere) continue;
            var who = stop.Character == "You" ? $"{you} (you)" : stop.Character;
            var count = stop.Items.Sum(i => i.Count);
            var color = stop.AlreadyThere ? Good : stop.HandOff ? Warn : Vector4.One;
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            var open = ImGui.CollapsingHeader($"{stop.Order}. {who}: {stop.Where}  ({count} item{(count == 1 ? "" : "s")})###tripStop{stop.Order}",
                ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.PopStyleColor();
            if (!open) continue;

            ImGui.Indent();
            ImGui.TextWrapped(stop.What);
            if (DateTime.UtcNow - stop.LastSeenUtc > TimeSpan.FromDays(plugin.Configuration.StaleAfterDays))
                ImGui.TextColored(Warn, $"Last checked {Ago(stop.LastSeenUtc)}; the contents may have changed.");
            foreach (var (id, stain, n) in stop.Items.OrderBy(i => ItemName(i.ItemId)))
                ImGui.BulletText($"{n} × {ItemName(id)}{(stain == 0 ? "" : $" ({stains!.Name(stain)})")}");
            ImGui.Unindent();
        }

        if (trip.Missing.Count > 0 && ImGui.CollapsingHeader($"Not owned by any character: {trip.Missing.Sum(m => m.Count)}###tripMissing"))
        {
            ImGui.Indent();
            ImGui.TextWrapped("None of your characters has these (as far as Refurnish has seen). Buy or craft them, or open any "
                              + "retainers that haven't been checked yet.");
            foreach (var (id, stain, n) in trip.Missing.OrderBy(m => ItemName(m.ItemId)))
                ImGui.BulletText($"{n} × {ItemName(id)}{(stain == 0 ? "" : $" ({stains!.Name(stain)})")}");
            ImGui.Unindent();
        }

        if (trip.WrongDye.Count > 0 && ImGui.CollapsingHeader($"Dyed differently from the layout: {trip.WrongDye.Sum(m => m.Count)}###tripDye"))
        {
            ImGui.Indent();
            ImGui.TextWrapped("These are owned, but in a different dye. They're included in the stops above; dye them if you want an exact match.");
            foreach (var (id, need, have, n) in trip.WrongDye.OrderBy(m => ItemName(m.ItemId)))
                ImGui.BulletText($"{n} × {ItemName(id)}: is {stains!.Name(have)}, layout wants {stains.Name(need)}");
            ImGui.Unindent();
        }
    }

    /// <summary>Warns about characters and retainers that haven't been checked, since items there won't be counted.</summary>
    private void DrawTripCoverage(List<CharacterCache> characters)
    {
        var gaps = new List<string>();
        foreach (var c in characters)
        {
            var name = string.IsNullOrEmpty(c.CharacterName) ? "(unnamed)" : c.CharacterName;
            var unseen = c.Retainers.Where(r => !c.Containers.ContainsKey($"retainer:{r.Key}")).Select(r => r.Value).ToList();
            if (c.Retainers.Count == 0) gaps.Add($"{name}: retainer list not loaded");
            else if (unseen.Count > 0) gaps.Add($"{name}: retainers not opened: {string.Join(", ", unseen)}");
        }
        if (gaps.Count == 0) return;
        ImGui.TextColored(Warn, "Some places haven't been checked, so items there aren't counted:");
        foreach (var g in gaps) ImGui.TextDisabled("   " + g);
    }
}
