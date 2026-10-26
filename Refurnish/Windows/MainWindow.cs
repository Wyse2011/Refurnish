using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;
using Refurnish.Automation;
using Refurnish.Inventory;
using Refurnish.Layout;
using Refurnish.Planning;

namespace Refurnish.Windows;

public sealed partial class MainWindow : Window
{
    private static readonly Vector4 Good = new(0.55f, 0.85f, 0.55f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.45f, 1f);

    private readonly Plugin plugin;
    private readonly Dictionary<uint, string> itemNames = new();
    private StainMatcher? stains;

    private LoadedLayout? layout;
    private readonly HashSet<(int, int)> excluded = new();
    private bool confirmPending;

    /// <summary>The layout currently loaded, used by the store executor's live re-check.</summary>
    public LoadedLayout? CurrentLayout => layout;
    private string? loadError;
    private ulong selectedHouse;

    private LayoutPlan? plan;
    private int planCacheVersion = -1;
    private ulong planHouse;
    private LoadedLayout? planLayout;

    public MainWindow(Plugin plugin) : base($"Refurnish v{Plugin.Version}###RefurnishMain")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void OnOpen()
    {
        stains ??= new StainMatcher();
        if (layout == null && File.Exists(plugin.Configuration.LastLayoutPath))
            LoadLayout(plugin.Configuration.LastLayoutPath);
    }

    public override void Draw()
    {
        stains ??= new StainMatcher();

        DrawLayoutBar();
        ImGui.Separator();

        var contentId = Plugin.PlayerState.IsLoaded ? Plugin.PlayerState.ContentId : 0;
        var cache = plugin.Cache.TryGet(contentId);
        if (cache == null)
        {
            ImGui.TextWrapped("Log in to a character to see your furnishings.");
            return;
        }

        DrawHousePicker(cache);

        if (ImGui.BeginTabBar("##tabs"))
        {
            if (layout != null && selectedHouse != 0)
            {
                RefreshPlan(cache);
                if (plan != null) DrawPlanTabs(plan);
            }

            if (ImGui.BeginTabItem("Data sources"))
            {
                DrawSources(cache);
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // ---- Layout file ----------------------------------------------------------------------

    private void DrawLayoutBar()
    {
        if (ImGui.Button("Load MakePlace layout..."))
        {
            var start = string.IsNullOrEmpty(plugin.Configuration.LastLayoutPath)
                ? null
                : Path.GetDirectoryName(plugin.Configuration.LastLayoutPath);
            plugin.FileDialog.OpenFileDialog("Choose a MakePlace layout", ".json", (ok, paths) =>
            {
                if (ok && paths.Count > 0) LoadLayout(paths[0]);
            }, 1, start);
        }

        ImGui.SameLine();
        if (layout == null)
        {
            ImGui.TextDisabled("No layout loaded.");
        }
        else
        {
            ImGui.TextUnformatted($"{layout.FileName}  ({layout.Interior.Count} interior items)");
            ImGui.SameLine();
            if (ImGui.SmallButton("Reload")) LoadLayout(layout.Path);
        }

        if (loadError != null) ImGui.TextColored(Bad, loadError);
    }

    private void LoadLayout(string path)
    {
        try
        {
            stains ??= new StainMatcher();
            layout = LoadedLayout.Load(path, stains);
            loadError = layout.Interior.Count == 0 ? "That layout has no interior furniture." : null;
            plugin.Configuration.LastLayoutPath = path;
            plugin.Configuration.Save();
            planLayout = null; // force recalculation
        }
        catch (Exception ex)
        {
            loadError = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}";
            Plugin.Log.Warning(ex, "Failed to load layout");
        }
    }

    // ---- Target house ---------------------------------------------------------------------

    private void DrawHousePicker(CharacterCache cache)
    {
        var current = plugin.Scanner.CurrentHouseId;
        if (selectedHouse == 0 && current != 0) selectedHouse = current;
        if (selectedHouse != 0 && !cache.Houses.ContainsKey(selectedHouse)) selectedHouse = 0;

        ImGui.TextUnformatted("Build in:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(320);
        var preview = selectedHouse != 0 ? cache.Houses[selectedHouse].Label : "Choose a house...";
        if (ImGui.BeginCombo("##house", preview))
        {
            foreach (var h in cache.Houses.Values.OrderByDescending(h => h.LastVisitedUtc))
            {
                var label = h.HouseId == current ? $"{h.Label} (you're here)" : h.Label;
                if (ImGui.Selectable($"{label}##{h.HouseId}", h.HouseId == selectedHouse))
                    selectedHouse = h.HouseId;
            }
            ImGui.EndCombo();
        }

        if (plugin.Scanner.InsideWithoutPermission)
            ImGui.TextColored(Warn, "You don't have decorating permission here, so Refurnish can't read or change this room. "
                                    + "In an FC house that usually means the main floor; your private chamber is tracked separately.");

        if (cache.Houses.Count == 0)
            ImGui.TextColored(Warn, "Go inside a house or room you can decorate so Refurnish can record it.");
        else if (selectedHouse != 0 && !cache.Containers.ContainsKey($"house:{selectedHouse}:placed"))
            ImGui.TextColored(Warn, "This room's placed furniture hasn't been read yet. Go inside it.");
    }

    // ---- Plan -----------------------------------------------------------------------------

    private void RefreshPlan(CharacterCache cache)
    {
        if (plan != null && planCacheVersion == plugin.Cache.Version && planHouse == selectedHouse && ReferenceEquals(planLayout, layout))
            return;

        plan = LayoutPlanner.Build(layout!.Interior, cache, selectedHouse);
        planCacheVersion = plugin.Cache.Version;
        planHouse = selectedHouse;
        planLayout = layout;
    }

    private void DrawPlanTabs(LayoutPlan p)
    {
        if (ImGui.BeginTabItem("Summary"))
        {
            DrawSummary(p);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Retrieve ({p.Count(PlanAction.Retrieve)})"))
        {
            DrawPlaceSection(p);
            ImGui.Separator();
            ImGui.TextUnformatted("Everything to bring out, by where it is:");
            DrawGroupedBySource(p.Of(PlanAction.Retrieve), showNeeded: true);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Store ({p.Count(PlanAction.Store)})"))
        {
            DrawStoreTab(p);
            ImGui.EndTabItem();
        }

        var problems = p.Count(PlanAction.Missing) + p.DyeMismatches;
        if (ImGui.BeginTabItem($"Missing / dye ({problems})"))
        {
            ImGui.TextColored(Bad, "Not found anywhere Refurnish has seen:");
            DrawItemTable("missing", p.Of(PlanAction.Missing), showNeeded: true);
            ImGui.Spacing();
            ImGui.TextColored(Warn, "Found, but the dye doesn't match the layout:");
            DrawItemTable("dye", p.Entries.Where(e => e.DyeMismatch), showNeeded: true, showSource: true);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Already placed ({p.Count(PlanAction.Keep)})"))
        {
            ImGui.TextWrapped("These stay. MakePlace will move them into position.");
            DrawItemTable("keep", p.Of(PlanAction.Keep), showNeeded: true);
            ImGui.EndTabItem();
        }
    }

    private void DrawSummary(LayoutPlan p)
    {
        var keep = p.Count(PlanAction.Keep);
        var retrieve = p.Count(PlanAction.Retrieve);
        var store = p.Count(PlanAction.Store);
        var missing = p.Count(PlanAction.Missing);

        ImGui.TextUnformatted($"The layout uses {layout!.Interior.Count} furnishings.");
        ImGui.BulletText($"{keep} are already placed here.");
        ImGui.BulletText($"{retrieve} need to be brought out ({p.NeedsTrip} of those need a trip to a retainer, the FC chest or another house).");
        ImGui.BulletText($"{store} placed items aren't in the new layout and should be stored.");
        if (missing > 0) ImGui.TextColored(Bad, $"{missing} can't be found anywhere you've been.");
        if (p.DyeMismatches > 0) ImGui.TextColored(Warn, $"{p.DyeMismatches} are found but dyed differently.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Space check");

        var roomFree = p.TargetStoreroom?.FreeSlots;
        var bagFree = p.PlayerInventory?.FreeSlots ?? 0;
        if (roomFree == null)
            ImGui.TextColored(Warn, "Storeroom space unknown. Open this house's storeroom once so Refurnish can read it.");
        else if (store <= roomFree)
            ImGui.TextColored(Good, $"The storeroom has {roomFree} free slots, enough for the {store} items to store.");
        else
            ImGui.TextColored(Warn, $"The storeroom has {roomFree} free slots; about {store - roomFree} items will have to go to your inventory.");

        if (p.NeedsTrip > bagFree)
            ImGui.TextColored(Warn, $"Your inventory has {bagFree} free slots, so fetching {p.NeedsTrip} items will take more than one trip.");
        else if (p.NeedsTrip > 0)
            ImGui.TextColored(Good, $"Your inventory has {bagFree} free slots, enough to fetch everything in one trip.");

        if (layout.InteriorMaterials.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextWrapped($"The layout also puts {layout.InteriorMaterials.Count} paintings or fish into frames and aquariums. "
                              + "Those aren't included in the counts above.");
        }

        ImGui.Spacing();
        if (ImGui.Button("Copy checklist to clipboard"))
            ImGui.SetClipboardText(BuildChecklist(p));
    }

    private void DrawGroupedBySource(IEnumerable<PlanEntry> entries, bool showNeeded)
    {
        var groups = entries
            .GroupBy(e => e.Source!.Key)
            .OrderBy(g => SourceOrder(g.First().Source!))
            .ThenBy(g => g.First().Source!.Label);

        foreach (var g in groups)
        {
            var src = g.First().Source!;
            var header = $"{src.Label}  -  {g.Count()} items  ({Ago(src.LastSeenUtc)})###{src.Key}";
            if (ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
                DrawItemTable(src.Key, g, showNeeded);
        }
    }

    private void DrawItemTable(string id, IEnumerable<PlanEntry> entries, bool showNeeded, bool showSource = false)
    {
        var rows = entries
            .GroupBy(e => (e.ItemId, Stain: showNeeded ? e.NeededStain : e.ActualStain, Have: e.ActualStain, Src: showSource ? e.Source?.Label : null))
            .Select(g => (g.Key, Count: g.Count()))
            .OrderBy(r => ItemName(r.Key.ItemId))
            .ToList();

        if (rows.Count == 0)
        {
            ImGui.TextDisabled("  Nothing here.");
            return;
        }

        var cols = showSource ? 4 : 3;
        if (!ImGui.BeginTable($"##{id}", cols, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Dye", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 40f);
        if (showSource) ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableHeadersRow();

        foreach (var (key, count) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ItemName(key.ItemId));

            ImGui.TableNextColumn();
            if (showNeeded && key.Stain != key.Have)
                ImGui.TextColored(Warn, $"{stains!.Name(key.Stain)} (have {stains.Name(key.Have)})");
            else
                ImGui.TextUnformatted(stains!.Name(key.Stain));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(count.ToString());

            if (showSource)
            {
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(key.Src ?? "");
            }
        }
        ImGui.EndTable();
    }

    private string BuildChecklist(LayoutPlan p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Refurnish checklist for {layout!.FileName}");
        sb.AppendLine();

        void Section(string title, IEnumerable<PlanEntry> list, Func<PlanEntry, byte> stain)
        {
            var rows = list.GroupBy(e => (e.ItemId, S: stain(e))).OrderBy(g => ItemName(g.Key.ItemId)).ToList();
            if (rows.Count == 0) return;
            sb.AppendLine(title);
            foreach (var g in rows)
            {
                var dye = g.Key.S == 0 ? "" : $" [{stains!.Name(g.Key.S)}]";
                sb.AppendLine($"  {g.Count()}x {ItemName(g.Key.ItemId)}{dye}");
            }
            sb.AppendLine();
        }

        Section("STORE (placed, not in new layout):", p.Of(PlanAction.Store), e => e.ActualStain);
        foreach (var g in p.Of(PlanAction.Retrieve).GroupBy(e => e.Source!.Key).OrderBy(g => SourceOrder(g.First().Source!)))
            Section($"RETRIEVE from {g.First().Source!.Label}:", g, e => e.NeededStain);
        Section("MISSING:", p.Of(PlanAction.Missing), e => e.NeededStain);
        Section("DYE DIFFERS (needs re-dyeing):", p.Entries.Where(e => e.DyeMismatch), e => e.NeededStain);
        return sb.ToString();
    }

    // ---- Store tab --------------------------------------------------------------------------

    private void DrawStoreTab(LayoutPlan p)
    {
        var exec = plugin.Executor;
        var learner = plugin.Learner;
        var inHouse = plugin.Scanner.CurrentHouseId == selectedHouse && selectedHouse != 0;
        var roomProfile = exec.ProfileFor(selectedHouse, StoreDestination.Storeroom);
        var bagProfile = exec.ProfileFor(selectedHouse, StoreDestination.Inventory);
        var canStore = inHouse && (roomProfile != null || bagProfile != null) && !learner.Active;

        DrawStoreSetup(inHouse, roomProfile, bagProfile);
        ImGui.Separator();

        var rows = p.Of(PlanAction.Store).Where(e => e.Item != null)
            .OrderBy(e => ItemName(e.ItemId)).ThenBy(e => e.Item!.Container).ThenBy(e => e.Item!.Slot)
            .ToList();
        var selected = rows.Where(e => !excluded.Contains((e.Item!.Container, e.Item.Slot))).ToList();

        // Run controls
        if (exec.Running)
        {
            var done = exec.DoneThisRun;
            var total = Math.Max(1, exec.TotalThisRun);
            ImGui.ProgressBar((float)done / total, new Vector2(-1, 0), $"Storing {done} of {exec.TotalThisRun}");
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("STOP", new Vector2(-1, 32))) exec.Stop("Stopped by you.");
            ImGui.PopStyleColor();
            ImGui.TextDisabled("You can also type /refurnish stop.");
        }
        else
        {
            ImGui.BeginDisabled(!canStore || selected.Count == 0);
            if (ImGui.Button($"Store all selected ({selected.Count})...", new Vector2(260, 28)))
            {
                confirmPending = true;
                ImGui.OpenPopup("##confirmStoreAll");
            }
            ImGui.EndDisabled();
            if (!canStore && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!inHouse ? "Go inside this house first." : "Set up storing for this house first (see Setup above).");

            ImGui.SameLine();
            if (ImGui.SmallButton("Select all")) excluded.Clear();
            ImGui.SameLine();
            if (ImGui.SmallButton("Select none"))
                foreach (var e in rows) excluded.Add((e.Item!.Container, e.Item.Slot));

            var delay = plugin.Configuration.StoreDelaySeconds;
            ImGui.SetNextItemWidth(200);
            if (ImGui.SliderFloat("Pause between items", ref delay, 0.5f, 5f, "%.1f s"))
            {
                plugin.Configuration.StoreDelaySeconds = delay;
                plugin.Configuration.Save();
            }
        }

        DrawConfirmPopup(selected);

        if (exec.StopReason != null) ImGui.TextColored(Bad, $"Stopped: {exec.StopReason}");
        else if (!exec.Running && exec.DoneThisRun > 0) ImGui.TextColored(Good, $"Done. Stored {exec.DoneThisRun} items.");

        ImGui.Spacing();
        if (rows.Count == 0)
        {
            ImGui.TextColored(Good, "Nothing to store. Everything placed here is part of the new layout.");
            return;
        }

        if (!ImGui.BeginTable("##storeRows", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Dye", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 2.5f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        foreach (var e in rows)
        {
            var key = (e.Item!.Container, e.Item.Slot);
            ImGui.PushID($"{key.Container}_{key.Slot}");
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var include = !excluded.Contains(key);
            ImGui.BeginDisabled(exec.Running);
            if (ImGui.Checkbox("##inc", ref include))
            {
                if (include) excluded.Remove(key);
                else excluded.Add(key);
            }
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ItemName(e.ItemId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(stains!.Name(e.ActualStain));
            ImGui.TableNextColumn();
            ImGui.TextDisabled($"Page {key.Container - LiveInventory.InteriorPlaced[0] + 1}, #{key.Slot + 1}");

            ImGui.TableNextColumn();
            var state = exec.StateOf(key.Container, key.Slot);
            if (state != null)
            {
                var (st, note) = state.Value;
                var color = st switch { JobState.Done => Good, JobState.Failed => Bad, _ => Warn };
                var text = st switch
                {
                    JobState.Queued => "Queued",
                    JobState.Working => "Storing...",
                    JobState.Done => note ?? "Done",
                    _ => "Failed",
                };
                ImGui.TextColored(color, text);
                if (note != null && st == JobState.Failed && ImGui.IsItemHovered()) ImGui.SetTooltip(note);
            }

            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!canStore || exec.Running);
            if (ImGui.SmallButton("Store"))
                exec.Enqueue(new[] { new StoreJob(selectedHouse, key.Container, key.Slot, e.ItemId, e.ActualStain) });
            ImGui.EndDisabled();
            if (!canStore && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(!inHouse ? "Go inside this room first." : "Finish the one-time setup at the top first.");

            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private void DrawConfirmPopup(List<PlanEntry> selected)
    {
        if (!ImGui.BeginPopup("##confirmStoreAll")) { confirmPending = false; return; }

        var hasRoom = plugin.Executor.ProfileFor(selectedHouse, StoreDestination.Storeroom) != null;
        var hasBag = plugin.Executor.ProfileFor(selectedHouse, StoreDestination.Inventory) != null;
        var roomFree = hasRoom ? LiveInventory.FreeSlots(LiveInventory.InteriorStoreroom) : 0;
        var bagFree = hasBag ? Math.Max(0, LiveInventory.FreeSlots(LiveInventory.PlayerInventory)) : 0;

        ImGui.TextUnformatted($"Put away {selected.Count} furnishings from this room?");
        ImGui.Spacing();
        if (hasRoom)
            ImGui.TextWrapped(roomFree >= 0
                ? $"Storeroom: {roomFree} free slots."
                : "The storeroom isn't loaded; open its tab in the furnishings window first or storing will stop.");
        if (hasBag)
            ImGui.TextWrapped($"Inventory: {bagFree} free slots{(hasRoom ? " (used once the storeroom is full)" : "")}.");
        var space = Math.Max(0, roomFree) + bagFree;
        if (selected.Count > space)
            ImGui.TextColored(Warn, $"Only room for about {space}. It will stop when everything is full.");
        ImGui.TextWrapped("Each item is re-checked before it's stored, and the run stops at the first problem.");
        ImGui.Spacing();

        if (ImGui.Button("Yes, store them", new Vector2(150, 0)) && confirmPending)
        {
            confirmPending = false;
            plugin.Executor.Enqueue(selected.Select(e => new StoreJob(selectedHouse, e.Item!.Container, e.Item.Slot, e.ItemId, e.ActualStain)));
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(100, 0)))
        {
            confirmPending = false;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawStoreSetup(bool inHouse, StoreProfile? roomProfile, StoreProfile? bagProfile)
    {
        var ready = roomProfile != null || bagProfile != null;

        if (!ready)
        {
            // Not set up yet: make the one required step impossible to miss.
            ImGui.Spacing();
            ImGui.TextColored(Warn, "One-time setup for this room is needed before Refurnish can store anything.");
            ImGui.TextWrapped("1. Press Learn below.");
            ImGui.TextWrapped("2. Open the game's furnishing menu and put away any one item yourself, using \"Store\" "
                              + "(to the storeroom) or \"Remove\" (to your inventory). In a private chamber or "
                              + "apartment the game may only offer Remove; that works too.");
            ImGui.TextWrapped("3. When Refurnish says \"Learned\", the Store buttons below unlock.");
            DrawLearnControls(inHouse);
            ImGui.Spacing();
            DrawCaptureLog();
            return;
        }

        if (!ImGui.CollapsingHeader("Setup: ready###storeSetup")) return;

        ImGui.TextColored(roomProfile != null ? Good : Vector4.One * 0.7f,
            roomProfile != null ? "Store to storeroom: ready" : "Store to storeroom: not set up");
        ImGui.TextColored(bagProfile != null ? Good : Vector4.One * 0.7f,
            bagProfile != null ? "Remove to inventory: ready" : "Remove to inventory: not set up (optional overflow)");
        ImGui.TextWrapped("Learn the other one too by pressing Learn and putting away an item the other way.");
        DrawLearnControls(inHouse);
        DrawCaptureLog();

        if (ImGui.SmallButton("Forget setup for this room"))
        {
            plugin.Configuration.StoreProfiles.RemoveAll(pr => pr.HouseId == selectedHouse);
            plugin.Configuration.Save();
        }
    }

    private void DrawLearnControls(bool inHouse)
    {
        var learner = plugin.Learner;
        if (learner.Active)
        {
            ImGui.TextColored(Warn, learner.Status);
            if (ImGui.Button("Cancel learning")) learner.Cancel();
            return;
        }

        ImGui.BeginDisabled(!inHouse || plugin.Executor.Running);
        if (ImGui.Button("Learn", new Vector2(120, 0))) learner.Start();
        ImGui.EndDisabled();
        if (!inHouse) { ImGui.SameLine(); ImGui.TextColored(Warn, "Go inside this room first."); }

        if (!string.IsNullOrEmpty(learner.Status))
            ImGui.TextColored(learner.LastSucceeded ? Good : Bad, learner.Status);
    }

    private void DrawCaptureLog()
    {
        var learner = plugin.Learner;
        if (learner.LastSucceeded || learner.LastCapture.Count == 0 || !ImGui.TreeNode("Capture log")) return;
        if (ImGui.SmallButton("Copy log"))
            ImGui.SetClipboardText(string.Join("\n", learner.LastCapture.Select(c => c.ToString())));
        foreach (var c in learner.LastCapture.TakeLast(30))
            ImGui.TextDisabled(c.ToString());
        ImGui.TreePop();
    }

    // ---- Data sources ---------------------------------------------------------------------

    private void DrawSources(CharacterCache cache)
    {
        ImGui.TextWrapped("Refurnish only knows what you've opened. Open each retainer at a summoning bell, view each "
                          + "FC chest compartment, and go inside each house (and open its storeroom) once. "
                          + "Contents are remembered after that and update whenever you look again.");
        ImGui.Spacing();

        var unseen = cache.Retainers.Where(r => !cache.Containers.ContainsKey($"retainer:{r.Key}")).Select(r => r.Value).ToList();
        if (cache.Retainers.Count == 0)
            ImGui.TextColored(Warn, "Retainer list not loaded yet. Use a summoning bell once.");
        else if (unseen.Count > 0)
            ImGui.TextColored(Warn, $"Retainers not opened yet: {string.Join(", ", unseen)}");

        var fcSeen = cache.Containers.Keys.Count(k => k.StartsWith("fc:"));
        if (fcSeen is > 0 and < 5)
            ImGui.TextColored(Warn, $"Only {fcSeen} of 5 FC chest compartments have been viewed (if your FC has more unlocked).");

        ImGui.Spacing();
        if (!ImGui.BeginTable("##sources", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Where", ImGuiTableColumnFlags.WidthStretch, 3f);
        ImGui.TableSetupColumn("Slots used", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
        ImGui.TableHeadersRow();

        string? forget = null;
        foreach (var c in cache.Containers.Values.OrderBy(SourceOrder).ThenBy(c => c.Label))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(c.Label);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{c.Items.Count} / {c.TotalSlots}");
            ImGui.TableNextColumn();
            var stale = DateTime.UtcNow - c.LastSeenUtc > TimeSpan.FromDays(plugin.Configuration.StaleAfterDays);
            if (stale) ImGui.TextColored(Warn, Ago(c.LastSeenUtc));
            else ImGui.TextUnformatted(Ago(c.LastSeenUtc));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"Forget##{c.Key}")) forget = c.Key;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this from the cache (e.g. a dismissed retainer or a house you no longer have).");
        }
        ImGui.EndTable();

        if (forget != null) plugin.Cache.Forget(cache.ContentId, forget);
    }

    // ---- Helpers --------------------------------------------------------------------------

    private string ItemName(uint id)
    {
        if (itemNames.TryGetValue(id, out var name)) return name;
        name = Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : $"Item #{id}";
        if (string.IsNullOrEmpty(name)) name = $"Item #{id}";
        itemNames[id] = name;
        return name;
    }

    private int SourceOrder(CachedContainer c) => c.Kind switch
    {
        SourceKind.HouseStoreroom when c.HouseId == selectedHouse => 0,
        SourceKind.Inventory => 1,
        SourceKind.Saddlebag => 2,
        SourceKind.HouseStoreroom => 3,
        SourceKind.Retainer => 4,
        SourceKind.FreeCompanyChest => 5,
        SourceKind.PlacedInHouse when c.HouseId == selectedHouse => 6,
        SourceKind.PlacedInHouse => 7,
        _ => 8,
    };

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 10) return "live";
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        return $"{(int)span.TotalDays} days ago";
    }
}
