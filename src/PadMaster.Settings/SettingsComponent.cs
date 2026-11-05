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
            PluginFormField.Choice("placement", "Placement", [new("right", "Right panel"), new("left", "Left sidebar"), new("floating", "Floating window")], settings.Placement),
            PluginFormField.Boolean("autoOpen", "Open at startup", settings.AutoOpen),
            PluginFormField.Boolean("confirmBulk", "Confirm bulk changes", settings.ConfirmBulk),
            PluginFormField.Integer("bulkThreshold", "Bulk threshold", settings.BulkThreshold, 1, int.MaxValue)
        ];
        return [new PluginConfigurationForm("padmaster", Name, new PluginFormDefinition(fields), Submit,
            "Tick Enable and press Save. The assistant panel opens inside LaunchBox; sign in there the first time. Change these settings here or from the panel's gear. Help: Plugins\\PadMaster\\README.md in your LaunchBox folder.")];
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
