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

This first version is **read-only**: it plans, you move the items. Automated retrieval is next.

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
