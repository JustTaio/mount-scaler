using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace MountScaler;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/mountscale";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration config;
    private readonly MountScaling scaling;
    private readonly WindowSystem windows = new("MountScaler");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (config.Version < 2)
        {
            // v1 measured against a Midlander; a strength tuned for that doesn't fit the new ratio.
            config.Strength = 1f;
            config.Version = 2;
            config.Save();
        }
        VanillaBaseline.Load();

        scaling = new MountScaling(config);
        configWindow = new ConfigWindow(config, scaling);
        windows.AddWindow(configWindow);

        CommandManager.AddHandler(Command, new CommandInfo((_, _) => configWindow.Toggle())
        {
            HelpMessage = "Open the Mount Scaler settings.",
        });

        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        CommandManager.RemoveHandler(Command);
        windows.RemoveAllWindows();
        scaling.Dispose();
    }
}
