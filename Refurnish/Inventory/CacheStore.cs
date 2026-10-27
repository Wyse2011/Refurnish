using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Refurnish.Inventory;

/// <summary>
/// Keeps one <see cref="CharacterCache"/> per character in memory and saves them as JSON files in
/// the plugin's config folder, so retainer and FC chest contents survive restarts.
/// </summary>
public sealed class CacheStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string folder;
    private readonly Dictionary<ulong, CharacterCache> caches = new();
    private readonly HashSet<ulong> dirty = new();
    private DateTime nextSave = DateTime.MinValue;

    /// <summary>Goes up whenever any cached contents change, so windows know to recalculate.</summary>
    public int Version { get; private set; }

    public CacheStore(string folder)
    {
        this.folder = folder;
        Directory.CreateDirectory(folder);
    }

    public CharacterCache? TryGet(ulong contentId) =>
        contentId != 0 ? GetOrCreate(contentId, null) : null;

    public CharacterCache GetOrCreate(ulong contentId, string? name)
    {
        if (!caches.TryGetValue(contentId, out var cache))
        {
            cache = Load(contentId) ?? new CharacterCache { ContentId = contentId };
            caches[contentId] = cache;
        }

        if (!string.IsNullOrEmpty(name) && cache.CharacterName != name)
        {
            cache.CharacterName = name;
            MarkDirty(contentId);
        }

        return cache;
    }

    /// <summary>Every character Refurnish has seen, loading any saved ones not yet in memory.</summary>
    public List<CharacterCache> All()
    {
        foreach (var file in Directory.EnumerateFiles(folder, "cache_*.json"))
        {
            var hex = Path.GetFileNameWithoutExtension(file)["cache_".Length..];
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var id) && id != 0)
                GetOrCreate(id, null);
        }
        return new List<CharacterCache>(caches.Values);
    }

    public void MarkDirty(ulong contentId)
    {
        dirty.Add(contentId);
        Version++;
    }

    /// <summary>Called from the framework tick; writes changed caches at most every 10 seconds.</summary>
    public void SaveIfDue()
    {
        if (dirty.Count == 0 || DateTime.UtcNow < nextSave) return;
        nextSave = DateTime.UtcNow.AddSeconds(10);
        SaveAll();
    }

    public void Forget(ulong contentId, string key)
    {
        if (caches.TryGetValue(contentId, out var c) && c.Containers.Remove(key))
            MarkDirty(contentId);
    }

    private void SaveAll()
    {
        foreach (var id in dirty)
        {
            if (!caches.TryGetValue(id, out var cache)) continue;
            try
            {
                var path = PathFor(id);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, JsonOptions));
                File.Move(tmp, path, true);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Couldn't save inventory cache for {id:X}");
            }
        }
        dirty.Clear();
    }

    private CharacterCache? Load(ulong contentId)
    {
        var path = PathFor(contentId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<CharacterCache>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, $"Inventory cache {path} was unreadable; starting fresh.");
            return null;
        }
    }

    private string PathFor(ulong contentId) => Path.Combine(folder, $"cache_{contentId:X16}.json");

    public void Dispose() => SaveAll();
}
