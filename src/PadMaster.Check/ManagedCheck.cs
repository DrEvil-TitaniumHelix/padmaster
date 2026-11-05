extern alias managed;
using System.IO;
using System.Runtime.CompilerServices;
using Unbroken.LaunchBox.Plugins.Services.Storage;
using PadMaster;

static class ManagedCheck
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "PadMaster-store-" + Guid.NewGuid().ToString("N"));
        var path = PackageIdentity.StorePath(root);
        var store = new FileStore(path);
        var component = new managed::PadMaster.Configuration.SettingsComponent(store);
        var forms = await component.GetConfigurationFormsAsync(default);
        Require(forms.Count == 1 && component.Name == PackageIdentity.Name, "one real Configure form");
        var form = forms.Single();
        Require(form.Description?.FallbackFormat == "Tick Enable and press Save. The assistant panel opens inside LaunchBox; sign in there the first time. Change these settings here or from the panel's gear. Help: Plugins\\PadMaster\\README.md in your LaunchBox folder.", "Configure header gives exact enable, sign-in and help instructions");
        Require(form.Form.Fields.Select(x => x.Id).SequenceEqual(new[] { "enabled", "assistant", "cli", "port", "placement", "autoOpen", "confirmBulk", "bulkThreshold" }), "Configure fields in order");
        Require(bool.TryParse(form.Form.Fields[0].DefaultValue, out var enabledDefault) && !enabledDefault, "default disabled");
        Require(form.Form.Fields.Single(x => x.Id == "confirmBulk").DefaultValue == "True" && form.Form.Fields.Single(x => x.Id == "bulkThreshold").DefaultValue == "20", "bulk defaults on and 20");
        var values = new Dictionary<string, string?> { ["enabled"] = "true", ["assistant"] = "claude-code", ["cli"] = @"C:\Tools\claude.exe", ["port"] = "47822", ["placement"] = "left", ["autoOpen"] = "false", ["confirmBulk"] = "false", ["bulkThreshold"] = "23" };
        Require((await form.SubmitAsync(values, default)).Succeeded, "form submit");
        var card = Settings.Read(path, _ => { });
        Require(card.Enabled && card.Cli == values["cli"] && card.Port == 47822 && card.Placement == "left" && !card.AutoOpen && !card.ConfirmBulk && card.BulkThreshold == 23, "form to card through physical store");
        values["port"] = "47823"; values["placement"] = "floating"; values["enabled"] = "false";
        Require(Settings.TryApply(card, values, false, out var changed, out _) && changed.Enabled, "card cannot change Enable");
        (changed with { Expanded = false }).Write(path);
        form = (await component.GetConfigurationFormsAsync(default)).Single();
        Require(form.Form.Fields.Single(x => x.Id == "port").DefaultValue == "47823" && form.Form.Fields.Single(x => x.Id == "placement").DefaultValue == "floating", "card to re-fetched form through same store");
        values["bulkThreshold"] = "0";
        Require(!(await form.SubmitAsync(values, default)).Succeeded && Settings.Read(path, _ => { }).BulkThreshold == 23, "invalid bulk threshold rejects without write");
        values["bulkThreshold"] = "23";
        values["port"] = "1";
        Require(!(await form.SubmitAsync(values, default)).Succeeded && Settings.Read(path, _ => { }).Port == 47823, "bad port rejected without write");
        values["port"] = "47824"; values["enabled"] = "true";
        Require((await form.SubmitAsync(values, default)).Succeeded && !Settings.Read(path, _ => { }).Expanded, "form preserves fresh expand state");
        values["assistant"] = "codex";
        Require((await form.SubmitAsync(values, default)).Succeeded && Settings.ReadRequired(path) is { Assistant: "codex", Cli: "" }, "form saves Codex selection and clears the previous assistant path");
        values["cli"] = "";
        form = (await component.GetConfigurationFormsAsync(default)).Single();
        Require(form.Form.Fields.Single(x => x.Id == "assistant").DefaultValue == "codex", "re-fetched form retains Codex selection");
        var saved = File.ReadAllBytes(path); var writes = store.Writes;
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Require(!(await form.SubmitAsync(values, default)).Succeeded, "G2 Configure aborts on locked store read");
        Require(store.Writes == writes && File.ReadAllBytes(path).SequenceEqual(saved) && Settings.ReadRequired(path).Enabled, "failed form read preserves enabled bytes");
        File.WriteAllText(path, "invalid");
        Require(!Settings.Read(path, _ => { }).Enabled, "corrupt card store defaults off");
        form = (await component.GetConfigurationFormsAsync(default)).Single();
        Require(bool.TryParse(form.Form.Fields[0].DefaultValue, out var corruptDefault) && !corruptDefault, "corrupt form store defaults off");
        foreach (var enable in new[] { true, false })
        {
            File.WriteAllText(path, "{\"expanded\":false,\"port\":\"invalid\"}"); values["enabled"] = enable.ToString();
            Require((await form.SubmitAsync(values, default)).Succeeded, "H1 Configure repairs corrupt content");
            var repaired = Settings.ReadRequired(path);
            Require(repaired.Enabled == enable && repaired.Expanded && repaired.Port == int.Parse(values["port"]!) && repaired.Cli == values["cli"] && repaired.Placement == values["placement"], "H1 repair honors Enable and form values, defaults omitted state");
        }
        Require(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == 1, "exactly one physical store file");
        Console.WriteLine("OK  managed: real API/component, eight fields, form/card atomic store round-trips, fresh values, bad port, locked read abort, corrupt store repair");
    }

    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    sealed class FileStore(string path) : IPluginDataStore
    {
        public int Writes { get; private set; }
        public Task<byte[]?> GetValueAsync(PluginDataStoreGetRequest request, CancellationToken token)
        {
            Require(request.Key == PackageIdentity.StoreKey, "store key");
            return Task.FromResult(File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        public Task SetValueAsync(PluginDataStoreSetRequest request, CancellationToken token)
        {
            Require(request.Key == PackageIdentity.StoreKey, "store key");
            Writes++; Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp"; File.WriteAllBytes(temporary, request.Value);
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            return Task.CompletedTask;
        }
        public Task RemoveValueAsync(PluginDataStoreRemoveRequest request, CancellationToken token) => throw new NotSupportedException();
    }
}
