using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace Refurnish.Inventory;

/// <summary>Which items are housing furnishings (indoor or outdoor), from the game's own housing tables.</summary>
public static class FurnishingCatalog
{
    private static HashSet<uint>? indoor;
    private static HashSet<uint>? outdoor;

    public static bool IsFurnishing(uint itemId) => IsIndoor(itemId) || IsOutdoor(itemId);

    public static bool IsIndoor(uint itemId)
    {
        if (indoor == null)
        {
            indoor = new HashSet<uint>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<HousingFurniture>())
                if (row.Item.RowId != 0) indoor.Add(row.Item.RowId);
        }
        return indoor.Contains(itemId);
    }

    public static bool IsOutdoor(uint itemId)
    {
        if (outdoor == null)
        {
            outdoor = new HashSet<uint>();
            foreach (var row in Plugin.DataManager.GetExcelSheet<HousingYardObject>())
                if (row.Item.RowId != 0) outdoor.Add(row.Item.RowId);
        }
        return outdoor.Contains(itemId);
    }
}
