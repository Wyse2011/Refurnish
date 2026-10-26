using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Refurnish.Automation;
using Refurnish.Inventory;
using Refurnish.Planning;

namespace Refurnish.Windows;

public sealed partial class MainWindow
{
    private readonly HashSet<(int, int)> placeExcluded = new();
    private bool placeConfirmPending;

    private void DrawPlaceSection(LayoutPlan p)
    {
        var placer = plugin.Placer;
        var learner = plugin.PlaceLearner;
        var inRoom = plugin.Scanner.CurrentHouseId == selectedHouse && selectedHouse != 0;

        var rows = p.Of(PlanAction.Retrieve)
            .Where(e => e.Item != null && p.IsAtHand(e.Source))
            .OrderBy(e => e.Source!.Kind).ThenBy(e => ItemName(e.ItemId))
            .ToList();

        if (!ImGui.CollapsingHeader($"Place from storeroom / inventory ({rows.Count})###placeSection", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        if (rows.Count == 0 && !placer.Running)
        {
            ImGui.TextDisabled("Nothing needed is in this room's storeroom or your inventory right now.");
            return;
        }

        var roomProfile = placer.ProfileFor(selectedHouse, PlaceSource.Storeroom);
        var bagProfile = placer.ProfileFor(selectedHouse, PlaceSource.Inventory);
        var needsRoom = rows.Any(e => e.Source!.Kind == SourceKind.HouseStoreroom);
        var needsBag = rows.Any(e => e.Source!.Kind == SourceKind.Inventory);
        var otherBusy = plugin.Executor.Running || plugin.Learner.Active || learner.Active;

        // ---- Setup ----
        var missingSetup = (needsRoom && roomProfile == null) || (needsBag && bagProfile == null);
        if (missingSetup || learner.Active || (!learner.LastSucceeded && !string.IsNullOrEmpty(learner.Status)))
        {
            ImGui.TextColored(Warn, "One-time setup for placing in this room:");
            if (needsRoom)
                ImGui.TextColored(roomProfile != null ? Good : Warn, roomProfile != null ? "  From storeroom: ready" : "  From storeroom: not set up");
            if (needsBag)
                ImGui.TextColored(bagProfile != null ? Good : Warn, bagProfile != null ? "  From inventory: ready" : "  From inventory: not set up");
            ImGui.TextWrapped("Press Learn, then place one item yourself from the storeroom (or inventory) using the game's menu. "
                              + "Anywhere is fine. Refurnish copies the exact message the game sends. Needed once per room and "
                              + "source, and again after each game patch.");

            if (learner.Active)
            {
                ImGui.TextColored(Warn, learner.Status);
                if (ImGui.Button("Cancel learning##place")) learner.Cancel();
            }
            else
            {
                ImGui.BeginDisabled(!inRoom || placer.Running || otherBusy);
                if (ImGui.Button("Learn##place", new Vector2(120, 0))) learner.Start();
                ImGui.EndDisabled();
                if (!inRoom) { ImGui.SameLine(); ImGui.TextColored(Warn, "Go inside this room first."); }
                if (!string.IsNullOrEmpty(learner.Status))
                    ImGui.TextColored(learner.LastSucceeded ? Good : Bad, learner.Status);
            }

            if (!learner.LastSucceeded && learner.LastCapture.Count > 0 && ImGui.TreeNode("Capture log##place"))
            {
                if (ImGui.SmallButton("Copy log##place"))
                    ImGui.SetClipboardText(string.Join("\n", learner.LastCapture.Select(c => c.ToString())));
                foreach (var c in learner.LastCapture.TakeLast(20))
                    ImGui.TextDisabled(c.ToString());
                ImGui.TreePop();
            }
            ImGui.Spacing();
        }

        // ---- Where to put things ----
        var atLayout = plugin.Configuration.PlaceAtLayoutPosition;
        if (ImGui.RadioButton("At its spot in the layout", atLayout)) { plugin.Configuration.PlaceAtLayoutPosition = true; plugin.Configuration.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Where I'm standing", !atLayout)) { plugin.Configuration.PlaceAtLayoutPosition = false; plugin.Configuration.Save(); }
        ImGui.TextDisabled(atLayout
            ? "Items go straight to their final position. Anything already placed still needs MakePlace to move it."
            : "Items are dropped at your feet for MakePlace to arrange.");

        bool CanPlace(PlanEntry e) => inRoom && !otherBusy &&
            (e.Source!.Kind == SourceKind.HouseStoreroom ? roomProfile != null : bagProfile != null);

        var selected = rows.Where(e => !placeExcluded.Contains((e.Item!.Container, e.Item.Slot)) && CanPlace(e)).ToList();

        // ---- Run controls ----
        if (placer.Running)
        {
            var total = Math.Max(1, placer.TotalThisRun);
            ImGui.ProgressBar((float)placer.DoneThisRun / total, new Vector2(-1, 0), $"Placing {placer.DoneThisRun} of {placer.TotalThisRun}");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("STOP##place", new Vector2(-1, 32))) placer.Stop("Stopped by you.");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("You can also type /refurnish stop.");
        }
        else
        {
            ImGui.BeginDisabled(selected.Count == 0);
            if (ImGui.Button($"Place all selected ({selected.Count})...", new Vector2(260, 28)))
            {
                placeConfirmPending = true;
                ImGui.OpenPopup("##confirmPlaceAll");
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.SmallButton("Select all##place")) placeExcluded.Clear();
            ImGui.SameLine();
            if (ImGui.SmallButton("Select none##place"))
                foreach (var e in rows) placeExcluded.Add((e.Item!.Container, e.Item.Slot));
        }

        DrawPlaceConfirm(selected);

        if (placer.StopReason != null) ImGui.TextColored(Bad, $"Stopped: {placer.StopReason}");
        else if (!placer.Running && placer.DoneThisRun > 0) ImGui.TextColored(Good, $"Done. Placed {placer.DoneThisRun} items.");

        // ---- Rows ----
        if (!ImGui.BeginTable("##placeRows", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Dye", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("From", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        foreach (var e in rows)
        {
            var key = (e.Item!.Container, e.Item.Slot);
            ImGui.PushID($"place_{key.Container}_{key.Slot}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var include = !placeExcluded.Contains(key);
            ImGui.BeginDisabled(placer.Running);
            if (ImGui.Checkbox("##inc", ref include))
            {
                if (include) placeExcluded.Remove(key);
                else placeExcluded.Add(key);
            }
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ItemName(e.ItemId));
            ImGui.TableNextColumn();
            if (e.DyeMismatch) ImGui.TextColored(Warn, $"{stains!.Name(e.ActualStain)} (layout: {stains.Name(e.NeededStain)})");
            else ImGui.TextUnformatted(stains!.Name(e.ActualStain));
            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.Source!.Kind == SourceKind.HouseStoreroom ? "Storeroom" : "Inventory");

            ImGui.TableNextColumn();
            var state = placer.StateOf(key.Container, key.Slot);
            if (state != null)
            {
                var (st, note) = state.Value;
                var color = st switch { JobState.Done => Good, JobState.Failed => Bad, _ => Warn };
                var text = st switch { JobState.Queued => "Queued", JobState.Working => "Placing...", JobState.Done => "Placed", _ => "Failed" };
                ImGui.TextColored(color, text);
                if (note != null && st == JobState.Failed && ImGui.IsItemHovered()) ImGui.SetTooltip(note);
            }

            ImGui.TableNextColumn();
            var can = CanPlace(e);
            ImGui.BeginDisabled(!can || placer.Running);
            if (ImGui.SmallButton("Place"))
                placer.Enqueue(new[] { new PlaceJob(selectedHouse, key.Container, key.Slot, e.ItemId, e.ActualStain) });
            ImGui.EndDisabled();
            if (!can && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!inRoom ? "Go inside this room first." : otherBusy ? "Another Refurnish action is running." : "Finish the one-time setup above first.");

            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawPlaceConfirm(List<PlanEntry> selected)
    {
        if (!ImGui.BeginPopup("##confirmPlaceAll")) { placeConfirmPending = false; return; }

        var free = LiveInventory.FreeSlots(LiveInventory.InteriorPlaced);
        ImGui.TextUnformatted($"Place {selected.Count} furnishings in this room?");
        ImGui.Spacing();
        ImGui.TextWrapped(plugin.Configuration.PlaceAtLayoutPosition
            ? "Each goes to its spot from the layout."
            : "Each is dropped where you're standing.");
        if (free >= 0 && selected.Count > free)
            ImGui.TextColored(Warn, $"This room only has about {free} free furnishing slots. It will stop when full.");
        ImGui.TextWrapped("Each item is re-checked before it's placed, and the run stops at the first problem.");
        ImGui.Spacing();

        if (ImGui.Button("Yes, place them", new Vector2(150, 0)) && placeConfirmPending)
        {
            placeConfirmPending = false;
            plugin.Placer.Enqueue(selected.Select(e => new PlaceJob(selectedHouse, e.Item!.Container, e.Item.Slot, e.ItemId, e.ActualStain)));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##place", new Vector2(100, 0)))
        {
            placeConfirmPending = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }
}
