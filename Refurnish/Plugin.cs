using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Refurnish.Inventory;
using Refurnish.Windows;

namespace Refurnish;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    private const string CommandName = "/refurnish";

    public Configuration Configuration { get; }
    public CacheStore Cache { get; }
    public InventoryScanner Scanner { get; }
    public FileDialogManager FileDialog { get; } = new();
    public WindowSystem WindowSystem { get; } = new("Refurnish");
    public MainWindow MainWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Cache = new CacheStore(PluginInterface.GetPluginConfigDirectory());
        Scanner = new InventoryScanner(Cache);

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Refurnish to plan a MakePlace layout swap.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += MainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= MainWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        Scanner.Dispose();
        Cache.Dispose();
    }

    private void OnCommand(string command, string args) => MainWindow.Toggle();

    private void DrawUi()
    {
        WindowSystem.Draw();
        FileDialog.Draw();
    }
}
