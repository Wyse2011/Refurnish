using System;
using System.Collections.Generic;
using Refurnish.Automation;
using Dalamud.Configuration;

namespace Refurnish;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Last MakePlace file loaded, reopened automatically next time.</summary>
    public string LastLayoutPath { get; set; } = string.Empty;

    /// <summary>Cached data older than this many days is flagged as possibly out of date.</summary>
    public int StaleAfterDays { get; set; } = 7;

    /// <summary>Learned store requests, one per house and destination.</summary>
    public List<StoreProfile> StoreProfiles { get; set; } = new();

    /// <summary>Pause between items when storing several. Never less than 0.5 seconds.</summary>
    public float StoreDelaySeconds { get; set; } = 1.5f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
