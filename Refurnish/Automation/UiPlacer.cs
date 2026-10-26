using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Refurnish.Inventory;
using Refurnish.Planning;

namespace Refurnish.Automation;

public enum PlaceSpot
{
    /// <summary>Where the game puts the preview by itself: on the floor in front of you.</summary>
    InFront,

    /// <summary>Spread around you so items don't land on each other.</summary>
    NearMe,

    /// <summary>The item's final position from the layout.</summary>
    Layout,
}

public enum UiPlaceMode
{
    /// <summary>Pick up the first item in the list, report it, and put it back. Places nothing.</summary>
    Test,
    One,
    All,
}

/// <summary>
/// Places items from the storeroom by clicking through the game's own furnishing window, exactly
/// as you would: pick an item in the storeroom list (which opens its menu), then choose "Place",
/// or close the menu if the item isn't wanted. The game does the placing itself, so its own state
/// stays consistent. The game chooses where the item goes; MakePlace then moves it into position.
///
/// It walks down the list. If an item is needed by the layout, it's placed; otherwise the menu is
/// closed and it moves on. If placing fails, the item is set aside and the walk continues; after
/// the end of the list it starts again from the top for the items that were missed.
///
/// After every click it waits for the game to respond. It stops if you leave the room, close the
/// furnishing window or leave the storeroom tab, if the game's status changes unexpectedly, if the
/// menu doesn't read exactly as learned, or after ten failures in a row.
/// </summary>
public sealed class UiPlacer
{
    private enum Step { Idle, Ready, WaitMenuOpen, WaitPreview, WaitPlaced, WaitMenuClosed }

    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PlaceTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReloadWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FlagGrace = TimeSpan.FromSeconds(5);
    private const int MaxPasses = 10;              // keeps going back to the top while passes still place things
    private const int MaxFailuresInARow = 30;
    private const float GroundBand = 0.3f;          // spots this close to your floor count as "on the floor"
    private static readonly TimeSpan Countdown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DiagnosticDelay = TimeSpan.FromSeconds(0.4);
    private static readonly TimeSpan PreviewTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PreviewSettle = TimeSpan.FromSeconds(0.25);
    private static readonly TimeSpan MinWaitAfterMenu = TimeSpan.FromSeconds(0.6); // you took about 1s; the preview came at 0.3s
    private const float MoveTolerance = 0.05f;

    private readonly Plugin plugin;
    private readonly List<string> log = new();
    private readonly HashSet<(int, int)> failedThisPass = new();
    private readonly HashSet<(int, int)> deferred = new();   // higher items held back until the floor items are in
    private int placedThisPass;
    private PlaceSpot? spotOverride;                          // set for the final "place the rest nearby" pass

    /// <summary>The spot mode in use right now (the final fallback pass places near you).</summary>
    private PlaceSpot EffectiveSpot => spotOverride ?? plugin.Configuration.UiPlaceSpot;

    /// <summary>Items placed near you in the fallback pass, for MakePlace to move (last run).</summary>
    public int PlacedNearby { get; private set; }

    /// <summary>True during the final pass that places leftover layout items near you.</summary>
    public bool InFallbackPass => spotOverride != null;
    private readonly Dictionary<(int, int), string> lastProblem = new();

    private Step step = Step.Idle;
    private UiPlaceMode mode;
    private ulong houseId;
    private int index;
    private DateTime stepStartedUtc;
    private DateTime nextAllowedUtc;
    private DateTime? flagsOddSinceUtc;
    private DateTime? emptySinceUtc;
    private HashSet<ConditionFlag> baselineFlags = new();
    private (int Container, int Slot) selected;
    private uint selectedItem;
    private byte selectedStain;
    private int placedCountBefore;
    private bool finishAfterClose;
    private int failuresInARow;
    private string? gameError;          // set when the game shows an error message while placing
    private string? previousGameError;  // the error from the last failed item
    private bool diagnosticLogged;
    private System.Numerics.Vector3 sentSpot;
    private int nearMeCount;
    private readonly HashSet<(int, int)> badCells = new();   // floor spots the game refused this run
    private int spotTries;                                    // different spots tried for the current item
    private const int MaxSpotTries = 5;
    private const float CellSize = 1.0f;
    private const float Clearance = 0.9f;
    private Layout.Requirement pendingNeed;
    private float heightShift; // added to layout heights when "match this floor" is on
    private readonly HashSet<(int, int)> noSpot = new();      // items whose layout spot can't be used; left in the storeroom
    private readonly List<string> noSpotItems = new();
    private DateTime? previewSeenUtc;
    private bool previewMoved;
    private DateTime previewMovedUtc;
    private bool toastHooked;

    public bool Running => step != Step.Idle;
    public int Pass { get; private set; } = 1;
    public int Placed { get; private set; }
    public int FinalFailures { get; private set; }
    public string? StopReason { get; private set; }
    public string? Summary { get; private set; }

    /// <summary>Items left in the storeroom because their layout spot couldn't be used (last run).</summary>
    public IReadOnlyList<string> NoSpotItems => noSpotItems;

    /// <summary>Seconds left before the first click, so you can aim the mouse at open floor.</summary>
    public double CountdownLeft => step == Step.Ready && Placed == 0 && index == 0 && Pass == 1
        ? Math.Max(0, (nextAllowedUtc - DateTime.UtcNow).TotalSeconds) : 0;

    public UiPlacer(Plugin plugin) => this.plugin = plugin;

    public List<string> RecentLog(int n) => log.Skip(Math.Max(0, log.Count - n)).ToList();

    /// <summary>Why Refurnish can't start right now, or null if it can.</summary>
    public string? CanStart(ulong targetHouse, UiPlaceMode placeMode)
    {
        var profile = plugin.Configuration.UiPlace;
        if (profile == null || !profile.IsValid) return "Placing through the furnishing window isn't set up (or the game was patched). Use Learn.";
        if (plugin.Scanner.CurrentHouseId != targetHouse || targetHouse == 0) return "Go inside this room first.";
        if (!CallbackTap.IsVisible(profile.ListAddon)) return "Open the furnishing window (Housing > Indoor Furnishings).";
        if (!CallbackTap.StoreroomTabShown()) return "Switch the furnishing window to its storeroom tab.";
        if (CallbackTap.IsVisible(profile.MenuAddon)) return "Close the open menu first.";
        if (plugin.Executor.Running || plugin.Learner.Active || plugin.UiLearner.Active) return "Another Refurnish action is running.";
        if (plugin.MainWindow.CurrentLayout == null && placeMode != UiPlaceMode.Test) return "Load a layout first.";
        if (Running) return "Already running.";
        return null;
    }

    public void Start(ulong targetHouse, UiPlaceMode placeMode)
    {
        var problem = CanStart(targetHouse, placeMode);
        if (problem != null) { StopReason = problem; return; }

        mode = placeMode;
        houseId = targetHouse;
        index = 0;
        Pass = 1;
        Placed = 0;
        FinalFailures = 0;
        failuresInARow = 0;
        failedThisPass.Clear();
        deferred.Clear();
        placedThisPass = 0;
        spotOverride = null;
        PlacedNearby = 0;
        lastProblem.Clear();
        noSpot.Clear();
        noSpotItems.Clear();
        log.Clear();
        StopReason = null;
        Summary = null;
        finishAfterClose = false;
        flagsOddSinceUtc = null;
        emptySinceUtc = null;
        baselineFlags = Plugin.Condition.AsReadOnlySet().ToHashSet();
        gameError = null;
        previousGameError = null;
        nearMeCount = 0;
        heightShift = plugin.MainWindow.LayoutHeightShift();
        if (heightShift != 0) Log($"Layout heights shifted by {heightShift:+0.00;-0.00} to match this floor.");
        badCells.Clear();
        spotTries = 0;
        nextAllowedUtc = DateTime.UtcNow + (placeMode == UiPlaceMode.Test ? TimeSpan.Zero : Countdown);
        step = Step.Ready;
        if (!toastHooked)
        {
            Plugin.ToastGui.ErrorToast += OnErrorToast;
            toastHooked = true;
        }
        Log(mode switch
        {
            UiPlaceMode.Test => "Test started: picking up the first storeroom item and putting it back.",
            UiPlaceMode.One => "Placing one item. Starting in 3 seconds.",
            _ => "Placing all needed storeroom items. Starting in 3 seconds.",
        });
    }

    private void OnErrorToast(ref SeString message, ref bool isHandled)
    {
        if (step == Step.WaitPlaced) gameError = message.TextValue;
    }

    private void Unhook()
    {
        if (!toastHooked) return;
        Plugin.ToastGui.ErrorToast -= OnErrorToast;
        toastHooked = false;
    }

    public void Stop(string reason)
    {
        if (step == Step.Idle) return;
        // If a menu we opened is still up, close it the same way Escape would.
        var profile = plugin.Configuration.UiPlace;
        if (profile != null && CallbackTap.IsVisible(profile.MenuAddon))
            CallbackTap.Fire(profile.MenuAddon, profile.CancelValues, profile.CancelClose);
        step = Step.Idle;
        Unhook();
        StopReason = reason;
        Log($"Stopped: {reason}");
    }

    public void Update()
    {
        if (step == Step.Idle) return;
        var profile = plugin.Configuration.UiPlace;
        if (profile == null || !profile.IsValid) { Stop("The learned clicks are no longer valid."); return; }

        // Checks that apply at every step.
        if (plugin.Scanner.CurrentHouseId != houseId) { Stop("You left the room."); return; }
        if (!CallbackTap.IsVisible(profile.ListAddon)) { Stop("The furnishing window was closed."); return; }

        switch (step)
        {
            case Step.Ready: StepReady(profile); break;
            case Step.WaitMenuOpen: StepWaitMenuOpen(profile); break;
            case Step.WaitPreview: StepWaitPreview(profile); break;
            case Step.WaitPlaced: StepWaitPlaced(profile); break;
            case Step.WaitMenuClosed: StepWaitMenuClosed(); break;
        }
    }

    private void StepReady(UiPlaceProfile profile)
    {
        if (DateTime.UtcNow < nextAllowedUtc) return;

        if (!CallbackTap.StoreroomTabShown()) { Stop("The furnishing window left the storeroom tab."); return; }
        if (CallbackTap.IsVisible(profile.MenuAddon)) { Stop("A menu is open that Refurnish didn't open."); return; }

        // The game's status flags must be what they were at the start (normal, using housing).
        var flags = Plugin.Condition.AsReadOnlySet();
        if (!flags.SetEquals(baselineFlags))
        {
            flagsOddSinceUtc ??= DateTime.UtcNow;
            if (DateTime.UtcNow - flagsOddSinceUtc > FlagGrace)
                Stop($"The game's status changed ({string.Join(", ", flags)}), so Refurnish stopped to be safe.");
            return;
        }
        flagsOddSinceUtc = null;

        // Wait out the moment the furnishing window reloads its lists.
        var storeroom = LiveInventory.ReadOccupied(LiveInventory.InteriorStoreroom, out var loaded);
        if (!loaded || storeroom.Count == 0)
        {
            emptySinceUtc ??= DateTime.UtcNow;
            if (DateTime.UtcNow - emptySinceUtc > ReloadWait) EndOfList();
            return;
        }
        emptySinceUtc = null;

        if (index >= storeroom.Count) { EndOfList(); return; }

        if (!CallbackTap.Fire(profile.ListAddon, profile.SelectFor(index), profile.SelectClose))
        {
            Stop("Couldn't click the furnishing list.");
            return;
        }
        step = Step.WaitMenuOpen;
        stepStartedUtc = DateTime.UtcNow;
    }

    private void StepWaitMenuOpen(UiPlaceProfile profile)
    {
        var menu = CallbackTap.OpenMenuStrings();
        if (menu == null)
        {
            if (DateTime.UtcNow - stepStartedUtc > MenuTimeout) EndOfList(); // past the end of the list
            return;
        }

        selected = CallbackTap.StoreroomSelection();
        if (!LiveInventory.InteriorStoreroom.Contains(selected.Container)
            || !LiveInventory.TryGetSlot(selected.Container, selected.Slot, out selectedItem, out selectedStain))
        {
            Log($"List entry {index + 1}: couldn't tell which item it was. Closing the menu.");
            CloseMenu(profile, finishAfter: true);
            return;
        }

        var name = ItemName(selectedItem);

        if (mode == UiPlaceMode.Test)
        {
            Log($"Test: list entry {index + 1} is {name} (storeroom {selected.Container}/{selected.Slot}). "
                + (profile.MenuChoiceMatches(menu, out var entry) ? $"The entry to choose reads \"{entry}\", as learned." : $"The entry to choose reads \"{entry}\" instead of \"{profile.PlaceEntryText}\".")
                + " Putting it back.");
            CloseMenu(profile, finishAfter: true);
            return;
        }

        var key = selected;
        if (failedThisPass.Contains(key) || noSpot.Contains(key)) { CloseAndNext(profile); return; }

        var cache = plugin.Cache.TryGet(Plugin.PlayerState.ContentId);
        var job = new PlaceJob(houseId, selected.Container, selected.Slot, selectedItem, selectedStain);
        var resolved = LiveStoreResolver.ResolvePlace(plugin.MainWindow.CurrentLayout, cache, job);
        if (resolved == null)
        {
            CloseAndNext(profile); // not needed by the layout
            return;
        }

        // Layout mode: put floor-standing items (tables, partitions, lofts) in first. Anything higher
        // usually sits on one of them, so it waits for the next pass. This isn't a failure.
        if (mode == UiPlaceMode.All && EffectiveSpot == PlaceSpot.Layout && Pass == 1)
        {
            var t = LayoutTarget(resolved.Value.Need, heightShift, out _);
            var me = Plugin.ObjectTable.LocalPlayer;
            if (t != null && me != null && t.Value.Y > me.Position.Y + GroundBand)
            {
                deferred.Add(key);
                CloseAndNext(profile);
                return;
            }
        }

        if (!profile.MenuChoiceMatches(menu, out var found))
        {
            Fail(key, $"{name}: the menu entry to choose reads \"{found}\" instead of \"{profile.PlaceEntryText}\", so it wasn't chosen.");
            if (step != Step.Idle) CloseAndNext(profile);
            return;
        }

        // Don't choose Place yet: the game first spawns a preview of the item on the floor, and
        // Place uses the preview's position. Wait for it.
        pendingNeed = resolved.Value.Need;
        previewSeenUtc = null;
        previewMoved = false;
        step = Step.WaitPreview;
        stepStartedUtc = DateTime.UtcNow;
    }

    private unsafe void StepWaitPreview(UiPlaceProfile profile)
    {
        var name = ItemName(selectedItem);
        if (!CallbackTap.IsVisible(profile.MenuAddon))
        {
            Fail(selected, $"{name}: the menu closed before Place could be chosen.");
            if (step != Step.Idle) { index++; step = Step.Ready; nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1); }
            return;
        }

        var preview = Preview();
        if (preview == null)
        {
            if (DateTime.UtcNow - stepStartedUtc > PreviewTimeout)
            {
                Fail(selected, $"{name}: the game didn't show a preview of the item, so Place wasn't chosen.");
                if (step != Step.Idle) CloseAndNext(profile);
            }
            return;
        }

        // Let it settle for a moment after it appears, and never act faster than a person would,
        // so a preview left over from an earlier item can't be mistaken for this one.
        previewSeenUtc ??= DateTime.UtcNow;
        if (DateTime.UtcNow - previewSeenUtc < PreviewSettle) return;
        if (DateTime.UtcNow - stepStartedUtc < MinWaitAfterMenu) return;

        var spotMode = EffectiveSpot;

        // "At its spot in the layout" means exactly that: if the spot can't be used, the item stays
        // in the storeroom (listed afterwards) rather than being dropped somewhere else.
        if (spotMode == PlaceSpot.Layout && !previewMoved)
        {
            var target = LayoutTarget(pendingNeed, heightShift, out var why);
            if (target == null)
            {
                SkipNoSpot(name, why);
                CloseAndNext(profile);
                return;
            }
            preview->SetPosition(target.Value.X, target.Value.Y, target.Value.Z);
            preview->SetRotation(pendingNeed.Rotation);
            sentSpot = target.Value;
            previewMoved = true;
            previewMovedUtc = DateTime.UtcNow;
            Log($"  {name}: moved the preview to its layout spot ({target.Value.X:0.00}, {target.Value.Y:0.00}, {target.Value.Z:0.00}).");
            return;
        }

        if (spotMode != PlaceSpot.InFront && !previewMoved)
        {
            var (spot, rotation, what) = ChooseSpot(pendingNeed, spotMode);
            if (float.IsNaN(rotation) && what.StartsWith("nowhere"))
            {
                Stop("There's no free floor left near you. Walk to another open part of the room and press Place all again; "
                     + "it carries on with what's left.");
                return;
            }
            if (!Sane(spot, rotation))
            {
                Fail(selected, $"{name}: the chosen spot looks wrong, so it wasn't used.");
                if (step != Step.Idle) CloseAndNext(profile);
                return;
            }
            preview->SetPosition(spot.X, spot.Y, spot.Z);
            preview->SetRotation(rotation);
            sentSpot = spot;
            previewMoved = true;
            previewMovedUtc = DateTime.UtcNow;
            Log($"  {name}: moved the preview {what}, to ({spot.X:0.00}, {spot.Y:0.00}, {spot.Z:0.00}).");
            return;
        }
        if (previewMoved && DateTime.UtcNow - previewMovedUtc < PreviewSettle) return;

        var at = new System.Numerics.Vector3(preview->Position.X, preview->Position.Y, preview->Position.Z);
        if (previewMoved && System.Numerics.Vector3.Distance(at, sentSpot) > MoveTolerance)
        {
            if (spotMode == PlaceSpot.Layout)
            {
                // Usually the surface it sits on isn't placed yet; a later pass tries again.
                Fail(selected, $"{name}: the game wouldn't keep it at its layout spot yet (moved it to ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00})); "
                               + "will try again after more items are placed.");
                if (step != Step.Idle) CloseAndNext(profile);
                return;
            }
            Log($"  {name}: the game moved the preview back to ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}); placing it there instead.");
        }
        if (!previewMoved)
            Log($"  {name}: preview is in front of you at ({at.X:0.00}, {at.Y:0.00}, {at.Z:0.00}).");
        sentSpot = at;

        placedCountBefore = LiveInventory.CountItem(LiveInventory.InteriorPlaced, selectedItem);
        gameError = null;
        diagnosticLogged = false;
        if (!CallbackTap.Fire(profile.MenuAddon, profile.PlaceValues, profile.PlaceClose))
        {
            Stop("Couldn't click Place in the menu.");
            return;
        }
        step = Step.WaitPlaced;
        stepStartedUtc = DateTime.UtcNow;
    }

    /// <summary>The preview object the game shows while an item is picked from the list.</summary>
    /// <summary>
    /// Is this height somewhere items can go in the current room? It must not be below the lowest
    /// of your feet and the furniture already here, nor more than 6 units above the highest of them
    /// (enough for wall-mounted items and stacking).
    /// </summary>
    public static bool FitsThisRoom(float y, out string why)
    {
        // Compare with the floor you're standing on. (Furniture can't be trusted for the low end:
        // something placed oddly in the past could be sitting below the floor.)
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) { why = string.Empty; return true; }
        var floor = me.Position.Y;
        var highest = floor;
        var furniture = LiveFurniture.Read();
        if (furniture != null)
            foreach (var f in furniture.Values)
                if (f.Position.Y >= floor - 0.5f) highest = MathF.Max(highest, f.Position.Y);

        if (y < floor - 0.5f) { why = $"at height {y:0.00}, below the floor you're on ({floor:0.00})"; return false; }
        if (y > highest + 6f) { why = $"at height {y:0.00}, far above anything here (highest {highest:0.00})"; return false; }
        why = string.Empty;
        return true;
    }

    private static unsafe FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* Preview()
    {
        var hm = HousingManager.Instance();
        if (hm == null || hm->IndoorTerritory == null) return null;
        return hm->IndoorTerritory->FurnitureManager.ObjectManager.ObjectArray.TempObject;
    }

    private static bool Sane(System.Numerics.Vector3 v, float r) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(r)
        && MathF.Abs(v.X) < 500 && MathF.Abs(v.Y) < 500 && MathF.Abs(v.Z) < 500;

    private void StepWaitPlaced(UiPlaceProfile profile)
    {
        var stillThere = LiveInventory.TryGetSlot(selected.Container, selected.Slot, out var id, out var stain)
                         && id == selectedItem && stain == selectedStain;
        var placed = LiveInventory.CountItem(LiveInventory.InteriorPlaced, selectedItem) > placedCountBefore;

        if (!diagnosticLogged && DateTime.UtcNow - stepStartedUtc > DiagnosticDelay)
        {
            diagnosticLogged = true;
            sentSpotForLog = sentSpot;
            Log(DescribeAim());
        }

        if (!stillThere && placed)
        {
            Placed++;
            placedThisPass++;
            if (spotOverride != null) PlacedNearby++;
            failuresInARow = 0;
            previousGameError = null;
            spotTries = 0;
            Log($"Placed {ItemName(selectedItem)}.");
            if (mode == UiPlaceMode.One) { Finish(); return; }
            // The list shifts up after an item leaves, so the same position now holds the next item.
            step = Step.Ready;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1.0); // placing keeps its own pace; the storing slider doesn't affect it
            return;
        }

        // The game said no (for example "Unable to place item."): no need to wait the full timeout.
        var refused = gameError != null && DateTime.UtcNow - stepStartedUtc > TimeSpan.FromSeconds(0.5);
        if (!refused && DateTime.UtcNow - stepStartedUtc <= PlaceTimeout) return;

        if (!diagnosticLogged) { diagnosticLogged = true; sentSpotForLog = sentSpot; Log(DescribeAim()); }
        var reason = gameError != null ? $"the game said \"{gameError}\"" : "the game didn't place it";

        // A full room isn't a spot problem: nothing more can go in.
        if (gameError != null && gameError.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            Stop($"The game said \"{gameError}\". This room can't take more furnishings.");
            return;
        }

        // When Refurnish chose the spot, a refusal usually means that spot is blocked (a wall, an
        // item it couldn't see). Remember it and try the same item at the next free spot.
        if (EffectiveSpot == PlaceSpot.NearMe && gameError != null && spotTries < MaxSpotTries)
        {
            badCells.Add(Cell(sentSpot));
            spotTries++;
            Log($"  That spot was refused; trying {ItemName(selectedItem)} at another spot ({spotTries}/{MaxSpotTries}).");
            gameError = null;
            if (CallbackTap.IsVisible(profile.MenuAddon)) CloseMenu(profile, finishAfter: false);
            else { step = Step.Ready; nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1); }
            return; // same list position, so the same item is picked again
        }
        spotTries = 0;

        Fail(selected, $"{ItemName(selectedItem)}: {reason}.");
        if (step == Step.Idle) return;

        // The same refusal twice in a row means it's the spot, not the item.
        // (Only "in front of me" uses the same spot every time; in layout mode a refusal often just
        // means the surface underneath isn't placed yet.)
        if (gameError != null && gameError == previousGameError && EffectiveSpot == PlaceSpot.InFront)
        {
            Stop($"The game refused twice in a row (\"{gameError}\"). The spot is probably blocked; "
                 + "try \"Near me\", or stand somewhere more open.");
            return;
        }
        previousGameError = gameError;

        if (mode == UiPlaceMode.One)
        {
            if (CallbackTap.IsVisible(profile.MenuAddon)) CloseMenu(profile, finishAfter: true);
            else Finish();
            return;
        }
        if (CallbackTap.IsVisible(profile.MenuAddon)) CloseAndNext(profile);
        else { index++; step = Step.Ready; nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1); }
    }

    private void StepWaitMenuClosed()
    {
        var profile = plugin.Configuration.UiPlace!;
        if (CallbackTap.IsVisible(profile.MenuAddon))
        {
            if (DateTime.UtcNow - stepStartedUtc > MenuTimeout) Stop("The menu didn't close.");
            return;
        }
        if (finishAfterClose) { Finish(); return; }
        step = Step.Ready;
        nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(0.3);
    }

    private void CloseAndNext(UiPlaceProfile profile)
    {
        index++;
        CloseMenu(profile, finishAfter: false);
    }

    private void CloseMenu(UiPlaceProfile profile, bool finishAfter)
    {
        finishAfterClose = finishAfter;
        if (!CallbackTap.Fire(profile.MenuAddon, profile.CancelValues, profile.CancelClose))
        {
            Stop("Couldn't close the menu.");
            return;
        }
        step = Step.WaitMenuClosed;
        stepStartedUtc = DateTime.UtcNow;
    }

    private void EndOfList()
    {
        if (mode == UiPlaceMode.Test) { Log("Test: the storeroom list looks empty."); Finish(); return; }
        if (mode == UiPlaceMode.One) { Log("Nothing in the storeroom list is needed by the layout."); Finish(); return; }

        // Go back to the top while that's still useful: some items were held back, or this pass placed
        // something (which may be the table another item needs).
        var remaining = failedThisPass.Count + deferred.Count;
        if (remaining > 0 && Pass < MaxPasses && (placedThisPass > 0 || deferred.Count > 0))
        {
            Log($"End of pass {Pass}: placed {placedThisPass}. Going back to the top for {remaining} item(s) still to place.");
            Pass++;
            failedThisPass.Clear();
            deferred.Clear();
            placedThisPass = 0;
            failuresInARow = 0;
            index = 0;
            step = Step.Ready;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            return;
        }

        // Layout mode: whatever couldn't go straight to its spot is placed nearby instead, so every
        // needed item ends up in the room and MakePlace can move it into position.
        var leftovers = failedThisPass.Count + noSpot.Count;
        if (mode == UiPlaceMode.All && spotOverride == null && plugin.Configuration.UiPlaceSpot == PlaceSpot.Layout
            && plugin.Configuration.LayoutFallbackNearMe && leftovers > 0)
        {
            Log($"{leftovers} item(s) couldn't go straight to their spot. Placing them near you so MakePlace can move them into position.");
            spotOverride = PlaceSpot.NearMe;
            Pass++;
            failedThisPass.Clear();
            noSpot.Clear();
            noSpotItems.Clear();
            placedThisPass = 0;
            failuresInARow = 0;
            index = 0;
            step = Step.Ready;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            return;
        }

        if (remaining > 0)
            Log(Pass >= MaxPasses
                ? $"Stopped after {MaxPasses} passes with {remaining} item(s) still unplaced."
                : $"Pass {Pass} placed nothing new, so the {remaining} remaining item(s) can't be placed right now.");
        FinalFailures = failedThisPass.Count;
        Finish();
    }

    private void Fail((int, int) key, string message)
    {
        failedThisPass.Add(key);
        lastProblem[key] = message;
        Log(message);
        if (++failuresInARow >= MaxFailuresInARow)
            Stop($"{MaxFailuresInARow} items in a row couldn't be placed, so something is wrong beyond a single item.");
    }

    private void Finish()
    {
        step = Step.Idle;
        Unhook();
        var leftover = noSpotItems.Count > 0 ? $" {noSpotItems.Count} stayed in the storeroom because their layout spot couldn't be used." : "";
        var nearby = PlacedNearby > 0
            ? $" {PlacedNearby} of those are near you because their spot wasn't free yet: apply the layout in MakePlace to move them into position."
            : "";
        Summary = mode == UiPlaceMode.Test
            ? "Test finished."
            : (FinalFailures > 0 ? $"Done. Placed {Placed}; {FinalFailures} couldn't be placed (see the log)." : $"Done. Placed {Placed}.") + nearby + leftover;
        Log(Summary);
    }

    /// <summary>Leaves an item in the storeroom because its layout spot can't be used. Not a malfunction, so it doesn't count toward stopping.</summary>
    private void SkipNoSpot(string name, string why)
    {
        noSpot.Add(selected);
        noSpotItems.Add($"{name}: {why}");
        Log($"  {name}: left in the storeroom; {why}.");
    }

    /// <summary>
    /// The item's layout spot (with the floor shift applied), or null with the reason if it can't be
    /// used here: the layout has no position for it, or the spot is below your floor or far above the room.
    /// </summary>
    public static System.Numerics.Vector3? LayoutTarget(Layout.Requirement need, float shift, out string why)
    {
        if (!need.HasPosition) { why = "the layout has no position for it"; return null; }
        var target = need.Position + new System.Numerics.Vector3(0, shift, 0);
        if (!FitsThisRoom(target.Y, out why)) return null;
        if (!Sane(target, need.Rotation)) { why = "its layout spot looks wrong"; return null; }
        return target;
    }

    /// <summary>Where to put the next item when Refurnish chooses the spot.</summary>
    private (System.Numerics.Vector3 Spot, float Rotation, string What) ChooseSpot(Layout.Requirement need, PlaceSpot mode)
    {
        if (mode == PlaceSpot.Layout && need.HasPosition)
        {
            var target = need.Position + new System.Numerics.Vector3(0, heightShift, 0);
            if (FitsThisRoom(target.Y, out var why))
                return (target, need.Rotation, heightShift != 0 ? "to its layout spot (shifted to this floor)" : "to its layout spot");
            Log($"  The layout spot is {why}, so this item goes near you instead. The layout may be for a different room.");
        }

        var free = FindFreeSpot();
        if (free != null)
            return (free.Value, 0f, mode == PlaceSpot.Layout ? "near you (the layout spot doesn't fit this room)" : "to free floor near you");
        return (System.Numerics.Vector3.Zero, float.NaN, "nowhere: no free floor left near you");
    }

    private static (int, int) Cell(System.Numerics.Vector3 v) => ((int)MathF.Round(v.X / CellSize), (int)MathF.Round(v.Z / CellSize));

    /// <summary>
    /// The nearest free floor spot inside the area the room's furniture covers (so it stays within
    /// the walls), at least a little way from every placed item, and not one the game already refused.
    /// </summary>
    private System.Numerics.Vector3? FindFreeSpot()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null) return null;
        var p = me.Position;

        var furniture = LiveFurniture.Read()?.Values.Select(f => f.Position).ToList() ?? new List<System.Numerics.Vector3>();
        var sameFloor = furniture.Where(f => MathF.Abs(f.Y - p.Y) < 1.5f).ToList();

        // The room's footprint, as far as the furniture shows it; at least a few steps around you.
        float minX = p.X - 3, maxX = p.X + 3, minZ = p.Z - 3, maxZ = p.Z + 3;
        foreach (var f in furniture)
        {
            minX = MathF.Min(minX, f.X); maxX = MathF.Max(maxX, f.X);
            minZ = MathF.Min(minZ, f.Z); maxZ = MathF.Max(maxZ, f.Z);
        }

        System.Numerics.Vector3? best = null;
        var bestDist = float.MaxValue;
        for (var x = MathF.Ceiling(minX / CellSize) * CellSize; x <= maxX; x += CellSize)
        for (var z = MathF.Ceiling(minZ / CellSize) * CellSize; z <= maxZ; z += CellSize)
        {
            var spot = new System.Numerics.Vector3(x, p.Y, z);
            if (badCells.Contains(Cell(spot))) continue;
            var d = MathF.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z));
            if (d < 1.0f || d >= bestDist) continue; // not on top of you
            if (sameFloor.Any(f => MathF.Abs(f.X - x) < Clearance && MathF.Abs(f.Z - z) < Clearance)) continue;
            best = spot;
            bestDist = d;
        }
        return best;
    }

    /// <summary>Where the game tried to put the item, where you are, and where the mouse is aiming.</summary>
    private static System.Numerics.Vector3 sentSpotForLog;

    private static unsafe string DescribeAim()
    {
        var sb = new System.Text.StringBuilder("  Spot check: ");
        var hm = HousingManager.Instance();
        if (hm != null && hm->IndoorTerritory != null)
        {
            var p = hm->IndoorTerritory->StoreroomItemPlacePosition;
            sb.Append($"game used ({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}), preview was at ({sentSpotForLog.X:0.00}, {sentSpotForLog.Y:0.00}, {sentSpotForLog.Z:0.00})");
        }
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me != null) sb.Append($", you at ({me.Position.X:0.00}, {me.Position.Y:0.00}, {me.Position.Z:0.00})");
        if (Plugin.GameGui.ScreenToWorld(ImGui.GetMousePos(), out var aim))
            sb.Append($", mouse aiming at ({aim.X:0.00}, {aim.Y:0.00}, {aim.Z:0.00})");
        else
            sb.Append(", mouse not aiming at anything in the room");
        return sb.ToString();
    }

    private void Log(string line)
    {
        log.Add($"{DateTime.Now:HH:mm:ss} {line}");
        if (log.Count > 200) log.RemoveAt(0);
    }

    private static string ItemName(uint id) =>
        Plugin.DataManager.GetExcelSheet<Item>().TryGetRow(id, out var row) ? row.Name.ExtractText() : $"item {id}";
}
