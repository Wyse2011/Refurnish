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
    private enum Step { Idle, Ready, WaitMenuOpen, WaitPlaced, WaitMenuClosed }

    private static readonly TimeSpan MenuTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan PlaceTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ReloadWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FlagGrace = TimeSpan.FromSeconds(5);
    private const int Passes = 2;
    private const int MaxFailuresInARow = 10;
    private static readonly TimeSpan Countdown = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DiagnosticDelay = TimeSpan.FromSeconds(0.4);

    private readonly Plugin plugin;
    private readonly List<string> log = new();
    private readonly HashSet<(int, int)> failedThisPass = new();
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
    private bool toastHooked;

    public bool Running => step != Step.Idle;
    public int Pass { get; private set; } = 1;
    public int Placed { get; private set; }
    public int FinalFailures { get; private set; }
    public string? StopReason { get; private set; }
    public string? Summary { get; private set; }

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
        lastProblem.Clear();
        log.Clear();
        StopReason = null;
        Summary = null;
        finishAfterClose = false;
        flagsOddSinceUtc = null;
        emptySinceUtc = null;
        baselineFlags = Plugin.Condition.AsReadOnlySet().ToHashSet();
        gameError = null;
        previousGameError = null;
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
            UiPlaceMode.One => "Placing one item. Move your mouse over open floor now; starting in 3 seconds.",
            _ => "Placing all needed storeroom items. Move your mouse over open floor and leave it there; starting in 3 seconds.",
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
                + (menu.SequenceEqual(profile.MenuStrings) ? "Menu matches what was learned." : $"Menu differs: {string.Join(" | ", menu)}")
                + " Putting it back.");
            CloseMenu(profile, finishAfter: true);
            return;
        }

        var key = selected;
        if (failedThisPass.Contains(key)) { CloseAndNext(profile); return; }

        var cache = plugin.Cache.TryGet(Plugin.PlayerState.ContentId);
        var job = new PlaceJob(houseId, selected.Container, selected.Slot, selectedItem, selectedStain);
        if (LiveStoreResolver.ResolvePlace(plugin.MainWindow.CurrentLayout, cache, job) == null)
        {
            CloseAndNext(profile); // not needed by the layout
            return;
        }

        if (!menu.SequenceEqual(profile.MenuStrings))
        {
            Fail(key, $"{name}: its menu doesn't match what was learned ({string.Join(" | ", menu)}), so Place wasn't chosen.");
            CloseAndNext(profile);
            return;
        }

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

    private void StepWaitPlaced(UiPlaceProfile profile)
    {
        var stillThere = LiveInventory.TryGetSlot(selected.Container, selected.Slot, out var id, out var stain)
                         && id == selectedItem && stain == selectedStain;
        var placed = LiveInventory.CountItem(LiveInventory.InteriorPlaced, selectedItem) > placedCountBefore;

        if (!diagnosticLogged && DateTime.UtcNow - stepStartedUtc > DiagnosticDelay)
        {
            diagnosticLogged = true;
            Log(DescribeAim());
        }

        if (!stillThere && placed)
        {
            Placed++;
            failuresInARow = 0;
            previousGameError = null;
            Log($"Placed {ItemName(selectedItem)}.");
            if (mode == UiPlaceMode.One) { Finish(); return; }
            // The list shifts up after an item leaves, so the same position now holds the next item.
            step = Step.Ready;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1.0, plugin.Configuration.StoreDelaySeconds));
            return;
        }

        // The game said no (for example "Unable to place item."): no need to wait the full timeout.
        var refused = gameError != null && DateTime.UtcNow - stepStartedUtc > TimeSpan.FromSeconds(0.5);
        if (!refused && DateTime.UtcNow - stepStartedUtc <= PlaceTimeout) return;

        if (!diagnosticLogged) { diagnosticLogged = true; Log(DescribeAim()); }
        var reason = gameError != null ? $"the game said \"{gameError}\"" : "the game didn't place it";
        Fail(selected, $"{ItemName(selectedItem)}: {reason}.");
        if (step == Step.Idle) return;

        // The same refusal twice in a row means it's the spot, not the item.
        if (gameError != null && gameError == previousGameError)
        {
            Stop($"The game refused twice in a row (\"{gameError}\"). This usually means your mouse isn't pointing at open floor. "
                 + "Aim it at an open spot, then try again.");
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

        if (failedThisPass.Count > 0 && Pass < Passes)
        {
            Log($"End of the list. Going back to the top for {failedThisPass.Count} missed item(s).");
            Pass++;
            failedThisPass.Clear();
            index = 0;
            step = Step.Ready;
            nextAllowedUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
            return;
        }

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
        Summary = mode == UiPlaceMode.Test
            ? "Test finished."
            : FinalFailures > 0 ? $"Done. Placed {Placed}; {FinalFailures} couldn't be placed (see the log)." : $"Done. Placed {Placed}.";
        Log(Summary);
    }

    /// <summary>Where the game tried to put the item, where you are, and where the mouse is aiming.</summary>
    private static unsafe string DescribeAim()
    {
        var sb = new System.Text.StringBuilder("  Spot check: ");
        var hm = HousingManager.Instance();
        if (hm != null && hm->IndoorTerritory != null)
        {
            var p = hm->IndoorTerritory->StoreroomItemPlacePosition;
            sb.Append($"game's placement spot ({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00})");
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
