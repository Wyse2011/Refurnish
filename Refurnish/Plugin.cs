using Dalamud.Game.Command;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Refurnish.Automation;
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
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    internal static string Version => typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Learned store requests are only trusted on the game version they were learned on.</summary>
    internal static string GameVersion =>
        DataManager.GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version : "unknown";

    private const string CommandName = "/refurnish";

    public Configuration Configuration { get; }
    public CacheStore Cache { get; }
    public InventoryScanner Scanner { get; }
    public CommandRecorder Recorder { get; }
    public StoreLearner Learner { get; }
    public StoreExecutor Executor { get; }
    public PacketRecorder Packets { get; }
    public PlaceLearner PlaceLearner { get; }
    public PlaceExecutor Placer { get; }
    public PlacementRecorder PlacementTest { get; }
    public CallbackTap Tap { get; }
    public UiPlaceLearner UiLearner { get; }
    public UiPlacer UiPlacer { get; }
    public FileDialogManager FileDialog { get; } = new();
    public WindowSystem WindowSystem { get; } = new("Refurnish");
    public MainWindow MainWindow { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        Cache = new CacheStore(PluginInterface.GetPluginConfigDirectory());
        Scanner = new InventoryScanner(Cache);
        Recorder = new CommandRecorder();
        Learner = new StoreLearner(this);
        Executor = new StoreExecutor(this);
        Packets = new PacketRecorder();
        if (PlaceExecutor.DirectPlacingEnabled) Packets.Enable(); // don't watch network traffic at all while placing is off
        PlaceLearner = new PlaceLearner(this);
        Placer = new PlaceExecutor(this);
        Tap = new CallbackTap();
        PlacementTest = new PlacementRecorder(Tap);
        UiLearner = new UiPlaceLearner(this);
        UiPlacer = new UiPlacer(this);
        Framework.Update += OnFrameworkUpdate;

        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Refurnish. \"/refurnish stop\" stops storing or placing immediately.",
        });

        PluginInterface.UiBuilder.Draw += DrawUi;
        PluginInterface.UiBuilder.OpenMainUi += MainWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= DrawUi;
        PluginInterface.UiBuilder.OpenMainUi -= MainWindow.Toggle;
        CommandManager.RemoveHandler(CommandName);
        Framework.Update -= OnFrameworkUpdate;
        Executor.Stop("Plugin unloaded.");
        Placer.Stop("Plugin unloaded.");
        Learner.Cancel();
        PlaceLearner.Cancel();
        Recorder.Dispose();
        Packets.Dispose();
        UiPlacer.Stop("Plugin unloaded.");
        UiLearner.Cancel();
        PlacementTest.Dispose();
        Tap.Dispose();

        WindowSystem.RemoveAllWindows();
        Scanner.Dispose();
        Cache.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("stop", System.StringComparison.OrdinalIgnoreCase))
        {
            Executor.Stop("Stopped with /refurnish stop.");
            Placer.Stop("Stopped with /refurnish stop.");
            UiPlacer.Stop("Stopped with /refurnish stop.");
            return;
        }
        MainWindow.Toggle();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            Learner.Update();
            PlaceLearner.Update();
            Executor.Update();
            Placer.Update();
            PlacementTest.Update();
            UiLearner.Update();
            UiPlacer.Update();
            Packets.Update();
        }
        catch (System.Exception ex)
        {
            Log.Error(ex, "Refurnish automation error");
            Executor.Stop("Something went wrong, so storing stopped. See /xllog for details.");
            Placer.Stop("Something went wrong, so placing stopped. See /xllog for details.");
            UiPlacer.Stop("Something went wrong, so placing stopped. See /xllog for details.");
            UiLearner.Cancel();
            Learner.Cancel();
            PlaceLearner.Cancel();
        }
    }

    private void DrawUi()
    {
        WindowSystem.Draw();
        FileDialog.Draw();
    }
}
