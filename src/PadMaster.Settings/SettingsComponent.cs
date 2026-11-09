using System.IO;
using System.Text.Json;
using Unbroken.LaunchBox.Plugins.Capabilities.Configuration;
using Unbroken.LaunchBox.Plugins.Components.GameDiscovery;
using Unbroken.LaunchBox.Plugins.Models.Forms;
using Unbroken.LaunchBox.Plugins.Services.Storage;

namespace PadMaster.Configuration;

public sealed class SettingsComponent(IPluginDataStore store) : IGameDiscoverySource, IPluginConfigurationFormsCapability
{
    public string Name => PackageIdentity.Name;
    public GameDiscoverySourceDescriptor Source { get; } = new("padmaster-assistant-settings", PackageIdentity.Name);

    async Task<Settings> Read(CancellationToken token)
    {
        try { return Settings.Decode(await store.GetValueAsync(new(PackageIdentity.StoreKey), token)); }
        catch (OperationCanceledException) { throw; }
        catch { return new(); }
    }

    public async Task<IReadOnlyCollection<PluginConfigurationForm>> GetConfigurationFormsAsync(CancellationToken cancellationToken)
    {
        var settings = await Read(cancellationToken);
        PluginFormField[] fields =
        [
            PluginFormField.Boolean("enabled", "Enable Padmaster AI Assistant", settings.Enabled),
            PluginFormField.Choice("assistant", "Assistant", [new("claude-code", "Claude Code"), new("codex", "Codex")], settings.Assistant.Length == 0 ? "claude-code" : settings.Assistant),
            PluginFormField.FilePath("cli", "CLI path", settings.Cli, "Blank = auto-detect the selected assistant."),
            PluginFormField.Integer("port", "Port", settings.Port, 1024, 65535),
            PluginFormField.Boolean("autoOpen", "Open the chat panel on its own", settings.AutoOpen, "At startup and after Save. Off = Pad Master runs with the assistant in the background; Tools > Padmaster AI Assistant opens the chat when you want it."),
            PluginFormField.Choice("placement", "Placement", [new("right", "Right panel"), new("left", "Left sidebar"), new("floating", "Floating window")], settings.Placement),
            PluginFormField.Boolean("confirmBulk", "Confirm bulk changes", settings.ConfirmBulk),
            PluginFormField.Integer("bulkThreshold", "Bulk threshold", settings.BulkThreshold, 1, int.MaxValue),
            PluginFormField.Text("menuCaption", "Pad Master right-click menu text", settings.MenuCaption, "What the game's right-click menu item says. Blank = " + Settings.DefaultMenuCaption + "."),
            PluginFormField.Choice("controller", "Controller to draw", Controllers(), settings.Controller, "The controller every picture is drawn on. To add your own, open any game's picture and choose \"Add my controller...\" in its dropdown.")
        ];
        return [new PluginConfigurationForm("padmaster", Name, new PluginFormDefinition(fields), Submit,
            "Enable turns Pad Master on: controller pictures, the PM badge, map generation and the chat. Until you tick it nothing runs. The chat panel opens from Tools > Padmaster AI Assistant when you want it, and has these same settings in its gear. The last two are Pad Master's own: the right-click item and the controller it draws. Help: Plugins\\PadMaster\\README.md in your LaunchBox folder.")];
    }

    // Local\Plugins\<name>\PadMaster.Settings.dll -> <root>\Plugins\PadMaster\controllers\<id>\anchors.json (displayName)
    public static List<PluginFormFieldOption> Controllers(string? pluginDirectory = null)
    {
        var options = new List<PluginFormFieldOption>(); string? fallback = null;   // "" = the shipped default (controllers\active.txt), named for the user, never by file
        try
        {
            // LaunchBox loads this DLL from a copy, so its own Location may be empty: find the plugin folder from LaunchBox's root instead.
            var folder = pluginDirectory ?? Candidates().FirstOrDefault(Directory.Exists);
            if (folder != null)
            {
                var controllers = Path.Combine(folder, "controllers"); var active = Path.Combine(controllers, "active.txt");
                var activeId = File.Exists(active) ? File.ReadAllText(active).Trim() : null;
                foreach (var dir in Directory.GetDirectories(controllers).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var anchors = Path.Combine(dir, "anchors.json"); if (!File.Exists(anchors)) continue;
                    string? name = null; try { name = JsonDocument.Parse(File.ReadAllText(anchors)).RootElement.TryGetProperty("displayName", out var n) ? n.GetString() : null; } catch (JsonException) { }
                    var id = Path.GetFileName(dir); options.Add(new(id, name ?? id));
                    if (string.Equals(id, activeId, StringComparison.OrdinalIgnoreCase) || (activeId == null && fallback == null)) fallback = name ?? id;
                }
            }
        }
        catch (Exception) { }   // the form must open whatever the disk says
        options.Insert(0, new("", fallback == null ? "Default" : "Default (" + fallback + ")"));
        return options;
    }

    static IEnumerable<string> Candidates()
    {
        var roots = new List<string>();
        var core = AppContext.BaseDirectory.TrimEnd('\\', '/'); roots.Add(Path.GetFullPath(Path.Combine(core, "..")));  // <LaunchBox>\Core -> <LaunchBox>
        roots.Add(core);
        var location = typeof(SettingsComponent).Assembly.Location; if (location.Length > 0 && Path.GetDirectoryName(location) is { } here) roots.Add(Path.GetFullPath(Path.Combine(here, "..", "..", "..")));
        try { if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } exeDir) { roots.Add(Path.GetFullPath(Path.Combine(exeDir, ".."))); roots.Add(exeDir); } } catch { }
        return roots.Select(r => Path.Combine(r, "Plugins", "PadMaster"));
    }

    async Task<PluginFormResult> Submit(IReadOnlyDictionary<string, string?> fields, CancellationToken token)
    {
        try
        {
            var bytes = await store.GetValueAsync(new(PackageIdentity.StoreKey), token);
            Settings current;
            try { current = Settings.Decode(bytes); }
            catch (JsonException) { current = new(); }
            if (!Settings.TryApply(current, fields, true, out var value, out var error)) return new(false, error);
            await store.SetValueAsync(new(PackageIdentity.StoreKey, value.Encode()), token); return new(true);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, "Settings could not be read or saved. Check Local/Plugins/.data and try again."); }
    }
}
