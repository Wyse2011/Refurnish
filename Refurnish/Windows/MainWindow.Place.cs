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

        if (!PlaceExecutor.DirectPlacingEnabled)
        {
            DrawUiPlacing(rows);
            return;
        }

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
                              + "Anywhere is fine, but turn it a little before setting it down (any angle except straight ahead). "
                              + "Refurnish checks where it really landed and finds the matching message the game sent. "
                              + "Needed once per room and source, and again after each game patch.");

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
            var passText = placer.Pass > 1 ? $"  -  second pass over missed items" : "";
            ImGui.ProgressBar((float)placer.DoneThisRun / total, new Vector2(-1, 0), $"Placed {placer.DoneThisRun} of {placer.TotalThisRun}{passText}");
            if (placer.SetAsideCount > 0)
                ImGui.TextColored(Warn, $"{placer.SetAsideCount} missed so far; they'll be tried again after the rest.");
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
        else if (!placer.Running && placer.TotalThisRun > 0)
        {
            ImGui.TextColored(Good, $"Done. Placed {placer.DoneThisRun} items.");
            if (placer.FinalFailures > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Bad, $"{placer.FinalFailures} couldn't be placed (hover their status for why).");
            }
        }

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
                var text = st switch
                {
                    JobState.Queued when note == null => "Queued",
                    JobState.Queued when note.StartsWith("Missed") => "Missed, retrying later",
                    JobState.Queued => "Retrying",
                    JobState.Working => note ?? "Placing...",
                    JobState.Done when note is null or "Placed" => "Placed",
                    JobState.Done => "Skipped",
                    _ => "Failed",
                };
                ImGui.TextColored(color, text);
                if (note != null && ImGui.IsItemHovered()) ImGui.SetTooltip(note);
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

    private bool uiConfirmPending;

    private void DrawUiPlacing(List<PlanEntry> rows)
    {
        var placer = plugin.UiPlacer;
        var learner = plugin.UiLearner;
        var profile = plugin.Configuration.UiPlace;
        var ready = profile is { IsValid: true };
        var inRoom = plugin.Scanner.CurrentHouseId == selectedHouse && selectedHouse != 0;
        var fromRoom = rows.Where(e => e.Source!.Kind == SourceKind.HouseStoreroom).ToList();
        var fromBag = rows.Where(e => e.Source!.Kind == SourceKind.Inventory).ToList();

        ImGui.TextWrapped("Refurnish clicks through the furnishing window's storeroom list for you, exactly as you would: "
                          + "it picks each needed item and chooses Place. The game decides where each item goes; "
                          + "afterwards, apply the layout with MakePlace to move everything into position.");
        ImGui.Spacing();

        // ---- Setup ----
        if (!ready || learner.Active)
        {
            ImGui.TextColored(Warn, ready ? "Learning again:" : "One-time setup (again after each game patch):");
            ImGui.TextWrapped("1. Open the furnishing window on its storeroom tab and press Learn.");
            ImGui.TextWrapped("2. Click an item in the storeroom list and choose Place (anywhere).");
            ImGui.TextWrapped("3. Click another item in the list and press Escape to close its menu.");
            if (learner.Active)
            {
                ImGui.TextColored(Warn, learner.Status);
                if (ImGui.Button("Cancel learning##ui")) learner.Cancel();
            }
            else
            {
                ImGui.BeginDisabled(!inRoom || placer.Running);
                if (ImGui.Button("Learn##ui", new Vector2(120, 0))) learner.Start();
                ImGui.EndDisabled();
                if (!inRoom) { ImGui.SameLine(); ImGui.TextColored(Warn, "Go inside this room first."); }
                if (!string.IsNullOrEmpty(learner.Status))
                    ImGui.TextColored(learner.LastSucceeded ? Good : Bad, learner.Status);
            }
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(Good, "Setup: ready.");
            ImGui.SameLine();
            ImGui.BeginDisabled(placer.Running);
            if (ImGui.SmallButton("Learn again##ui")) learner.Start();
            ImGui.EndDisabled();
            if (!learner.LastSucceeded && !string.IsNullOrEmpty(learner.Status) && learner.Status != "Cancelled.")
                ImGui.TextColored(Bad, $"Last learn attempt: {learner.Status} (the earlier setup is still used)");
        }

        // ---- Run ----
        if (placer.Running)
        {
            var countdown = placer.CountdownLeft;
            if (countdown > 0)
                ImGui.TextColored(Warn, $"Starting in {Math.Ceiling(countdown)}...");
            else
                ImGui.TextColored(Warn, $"Working... placed {placer.Placed} so far{(placer.Pass > 1 ? $" (pass {placer.Pass})" : "")}.");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("STOP##ui", new Vector2(-1, 32))) placer.Stop("Stopped by you.");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("You can also type /refurnish stop. Don't click in the furnishing window while it works.");
        }
        else if (ready)
        {
            var testProblem = placer.CanStart(selectedHouse, UiPlaceMode.Test);
            var placeProblem = placer.CanStart(selectedHouse, UiPlaceMode.All);

            ImGui.BeginDisabled(testProblem != null);
            if (ImGui.Button("Test: pick up and put back", new Vector2(220, 0))) placer.Start(selectedHouse, UiPlaceMode.Test);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(placeProblem != null || fromRoom.Count == 0);
            if (ImGui.Button("Place one", new Vector2(110, 0))) placer.Start(selectedHouse, UiPlaceMode.One);
            ImGui.SameLine();
            if (ImGui.Button($"Place all needed ({fromRoom.Count})...", new Vector2(220, 0)))
            {
                uiConfirmPending = true;
                ImGui.OpenPopup("##confirmUiPlaceAll");
            }
            ImGui.EndDisabled();

            var problem = testProblem ?? placeProblem;
            if (problem != null) ImGui.TextColored(Warn, problem);
            var spot = plugin.Configuration.UiPlaceSpot;
            void SpotOption(string label, PlaceSpot value)
            {
                if (ImGui.RadioButton(label, spot == value)) { plugin.Configuration.UiPlaceSpot = value; plugin.Configuration.Save(); }
            }
            SpotOption("In front of me##ui", PlaceSpot.InFront);
            ImGui.SameLine();
            SpotOption("Near me, spread out##ui", PlaceSpot.NearMe);
            ImGui.SameLine();
            SpotOption("At its spot in the layout##ui", PlaceSpot.Layout);
            ImGui.TextWrapped(spot switch
            {
                PlaceSpot.InFront => "Each item goes where the game puts its preview: on the floor in front of you. "
                                     + "Good for testing; for many items, step aside between them or use another option.",
                PlaceSpot.NearMe => "Items are spread around where you're standing, for MakePlace to arrange.",
                _ => "Each item goes straight to its final position. If an item's spot can't be used here, it stays in the storeroom "
                     + "and is listed afterwards. Items that were already placed still need MakePlace to move them.",
            });
            if (spot == PlaceSpot.Layout) DrawHeightCheck();
            ImGui.TextWrapped("Keep the furnishing window on its storeroom tab and don't click in it while Refurnish works. "
                              + "To stop, type /refurnish stop.");
        }

        if (ImGui.BeginPopup("##confirmUiPlaceAll"))
        {
            ImGui.TextUnformatted($"Place {fromRoom.Count} needed items from the storeroom?");
            ImGui.TextWrapped("Refurnish goes down the storeroom list, places what the layout needs and skips the rest. "
                              + "With layout spots, floor-standing items go in first and things that sit on them follow in later passes. "
                              + "It keeps going back to the top while passes still place something, and stops if anything unexpected happens.");
            if (ImGui.Button("Yes, place them##ui", new Vector2(150, 0)) && uiConfirmPending)
            {
                uiConfirmPending = false;
                placer.Start(selectedHouse, UiPlaceMode.All);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##uiconfirm", new Vector2(100, 0)))
            {
                uiConfirmPending = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        else
        {
            uiConfirmPending = false;
        }

        if (placer.StopReason != null && !placer.Running) ImGui.TextColored(Bad, $"Stopped: {placer.StopReason}");
        else if (placer.Summary != null && !placer.Running) ImGui.TextColored(placer.FinalFailures > 0 ? Warn : Good, placer.Summary);

        if (!placer.Running && placer.NoSpotItems.Count > 0)
        {
            if (ImGui.TreeNodeEx($"Left in the storeroom: {placer.NoSpotItems.Count} (their layout spot couldn't be used)###uiNoSpot"))
            {
                foreach (var line in placer.NoSpotItems) ImGui.BulletText(line);
                ImGui.TreePop();
            }
            ImGui.BeginDisabled(placer.CanStart(selectedHouse, UiPlaceMode.All) != null);
            if (ImGui.SmallButton("Place what's left near me##uiLeftovers"))
            {
                plugin.Configuration.UiPlaceSpot = PlaceSpot.NearMe;
                plugin.Configuration.Save();
                placer.Start(selectedHouse, UiPlaceMode.All);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Switches to \"Near me, spread out\" and places everything still needed from the storeroom, for MakePlace to arrange.");
        }

        var recent = placer.RecentLog(12);
        if (recent.Count > 0 && ImGui.TreeNodeEx("Activity###uiPlaceLog", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var line in recent) ImGui.TextDisabled(line);
            ImGui.TreePop();
        }

        // ---- What's waiting ----
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted($"In the storeroom and needed: {fromRoom.Count}");
        if (fromBag.Count > 0)
        {
            ImGui.TextUnformatted($"In your inventory and needed (place these by hand for now): {fromBag.Count}");
            foreach (var g in fromBag.GroupBy(e => (e.ItemId, e.ActualStain)).OrderBy(g => ItemName(g.Key.ItemId)))
            {
                var dye = g.Key.ActualStain == 0 ? "" : $" ({stains!.Name(g.Key.ActualStain)})";
                ImGui.BulletText($"{g.Count()}x {ItemName(g.Key.ItemId)}{dye}");
            }
        }
    }

    private void DrawPlacementTest()
    {
        var rec = plugin.PlacementTest;
        ImGui.TextColored(Warn, "Placement test (recording only)");
        ImGui.TextWrapped("This only watches; it never sends anything to the game. It records what the game itself does "
                          + "while you place an item by hand, so placing can be rebuilt on the game's own steps.");
        ImGui.TextWrapped("Side-by-side test (Refurnish's attempt, then yours):");
        ImGui.TextWrapped("1. Open the furnishing window on its storeroom tab, then press Start recording here.");
        ImGui.TextWrapped("2. Go to the Retrieve tab and press Place one (Near me). Wait until it says the game refused.");
        ImGui.TextWrapped("3. Now place one item yourself: click it in the storeroom list, choose Place, and set it down on the floor.");
        ImGui.TextWrapped("4. Come back here, press Stop, then Copy log, and paste it into the chat.");
        ImGui.TextDisabled("Recording keeps going while you switch tabs. It only watches; it never sends anything.");
        if (rec.SetupError != null) { ImGui.TextColored(Bad, rec.SetupError); return; }

        if (rec.Recording)
        {
            ImGui.TextColored(Warn, $"Recording... ({rec.LineCount} lines)");
            if (ImGui.Button("Stop recording", new Vector2(160, 0))) rec.Stop();
        }
        else
        {
            if (ImGui.Button("Start recording", new Vector2(160, 0))) rec.Start();
            ImGui.SameLine();
            ImGui.BeginDisabled(rec.LineCount == 0);
            if (ImGui.Button("Copy log", new Vector2(120, 0))) ImGui.SetClipboardText(rec.Text());
            ImGui.EndDisabled();
        }

        ImGui.Separator();
        foreach (var line in rec.Tail(25))
            ImGui.TextDisabled(line);
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
        ImGui.TextWrapped("If an item can't be placed, it's set aside and the next one is tried. After the whole list, "
                          + "it goes back to the top for the ones that were missed. Every placement is checked against where it "
                          + "actually landed; if that's ever wrong, everything stops. It also stops if you leave the room, "
                          + "the room is full, or ten items in a row fail.");
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
