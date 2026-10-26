# Refurnish

A housing layout planner for Dalamud. Load a MakePlace layout and Refurnish tells you exactly
what to do to switch your house over to it:

- **Already placed:** furnishings in the house that the new layout uses. MakePlace will move these.
- **Store:** furnishings in the house that the new layout doesn't use.
- **Retrieve:** furnishings the layout needs, grouped by where they are (this house's storeroom,
  your inventory, saddlebag, another house's storeroom, each retainer, each FC chest compartment).
- **Missing / dye:** what you don't own, and what you own but dyed a different color.

It matches dyes from the MakePlace file, prefers copies that are already close at hand, checks
storeroom and inventory space, and can copy the whole plan to your clipboard as a checklist.

It can also **store the furnishings the new layout doesn't use**, one at a time or all at once,
with safety checks on every item, and **place the furnishings the layout needs** straight from
your storeroom or inventory, at their final spot from the layout (see below).

## Install (for players)

Copy this custom repository URL:

```
https://raw.githubusercontent.com/Wyse2011/Refurnish/main/repo.json
```

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste the URL into the empty box, click the **+**
   button, make sure the checkbox next to it is ticked, then click **Save and Close**.
3. Type `/xlplugins`, search for **Refurnish**, and click **Install**.
4. Type `/refurnish` to open it.

Refurnish is a third-party plugin and is not affiliated with Square Enix or MakePlace.

## How to use it

Refurnish can only see what the game has loaded, so it remembers things as you visit them:

1. **Retainers:** open each retainer at a summoning bell once.
2. **FC chest:** open the chest and click through each compartment once.
3. **Houses:** go inside each house you can decorate, and open its storeroom once.

The **Data sources** tab shows what's been recorded and when, and lists any retainers you haven't
opened yet. Contents update automatically whenever you look again.

Then click **Load MakePlace layout...**, pick the `.json` file, choose the house to build in, and
work through the tabs. When everything is placed, load the same file in the MakePlace plugin to
move it all into position.

Paintings in frames and fish in aquariums are counted separately and not yet planned.

## Storing unneeded furnishings

The **Store** tab lists every placed item the new layout doesn't use. You can press **Store** on
a single row, or tick the rows you want and press **Store all selected**, which asks you to
confirm first.

Each room is tracked on its own: an FC house's main floor, each private chamber, and each
apartment have their own furniture and permissions. Refurnish can only read and change rooms
where you have decorating permission.

**One-time setup per room.** Refurnish never guesses what to send the game. Press **Learn**,
then put away any one furnishing yourself with the game's own menu, using "Store" or "Remove".
In a private chamber or apartment the game may only offer "Remove" (to inventory); that works too. Refurnish records the exact
request the game sent and reuses it, changing only which item it points at. Do the same with
"Remove" (to inventory) if you want items to overflow into your bags when the storeroom is full.
Setup is tied to the house and to the game version, so it needs redoing after a patch.

**Safety checks on every item:**

- You must be inside the house the plan is for, with the storeroom loaded.
- Right before each item, the plan is recalculated from a live read of the house. If the layout
  now needs that item, it's skipped.
- The exact slot is checked to still hold that item and dye.
- There must be a free storeroom slot (or inventory slot, if overflow is set up).
- After each request, Refurnish waits for the game to confirm the item actually moved. If it
  doesn't within a few seconds, everything stops. Nothing is retried automatically.
- One item at a time, with a pause between items (1.5 seconds by default, never under 0.5).
- **STOP** button in the window, or type `/refurnish stop`.

## Placing furnishings from the storeroom or inventory

At the top of the **Retrieve** tab, everything the layout needs that's already in this room's
storeroom or your inventory gets a **Place** button, plus **Place all selected** with a
confirmation.

Choose where items go:

- **At its spot in the layout** (default): straight to the final position and rotation from the
  MakePlace file. Items that were already placed still need the MakePlace plugin to move them.
- **Where I'm standing:** dropped at your feet for MakePlace to arrange.

**One-time setup per room and source.** Press **Learn** in that section, then place one item
yourself from the storeroom (or inventory) with the game's menu, anywhere. The game's network
message IDs change every patch, so Refurnish copies the exact message the game sent and only
swaps the item, position and rotation. It's tied to the room and game version, and in some cases
only to the current game session (the window says so).

Before every item Refurnish re-plans from a live read of the room, checks the slot still holds
that exact item, checks there's a free furnishing slot and that the position is sensible, and
afterwards waits for the item to actually appear in the room. Any problem stops the run.

---

# Building from source (for developers)

## Requirements

- XIVLauncher with Dalamud, and the game launched at least once with Dalamud enabled.
- The .NET SDK version your Dalamud API level uses. If it's wrong, the build error will name the
  version it wants.

## Build and load locally

```
cd Refurnish
dotnet build -c Debug
```

Add the full path to `Refurnish/bin/x64/Debug/Refurnish.dll` under **Dev Plugin Locations** in
`/xlsettings` > **Experimental**, then enable it under **Dev Tools > Installed Dev Plugins** in
`/xlplugins`.

## Publish a release

Run **Actions > Build and Release > Run workflow** on GitHub. It builds the plugin and attaches
`latest.zip` to a new release, which is what `repo.json` points to.

To ship an update, bump `<Version>` in `Refurnish/Refurnish.csproj` and `AssemblyVersion` in
`repo.json` to the same new number, then run the workflow again.

## Project layout

- `Layout/` reads MakePlace files and maps their dye colors back to in-game dyes.
- `Inventory/` reads loaded game containers once a second and caches them per character.
- `Planning/` compares the layout against the cache. Pure logic, no game calls.
- `Windows/` is the ImGui interface.

## Acknowledgements

- [MakePlace](https://jawslouis.itch.io/makeplace) and the
  [MakePlace plugin](https://github.com/jawslouis/MakePlacePlugin) by jawslouis, whose layout
  format Refurnish reads.
- [Dalamud](https://github.com/goatcorp/Dalamud) and
  [FFXIVClientStructs](https://github.com/aers/FFXIVClientStructs).
