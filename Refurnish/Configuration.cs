using System;
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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
