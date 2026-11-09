using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace PadMaster;

public sealed record ActivationState(Settings Settings, bool Listed, bool Readable, bool CorruptStore = false)
{
    public bool Active => Readable && !Listed && Settings.Enabled;
    public string Notice => CorruptStore ? "Padmaster AI Assistant settings are invalid. Open Configure in Tools > Manage > Plugins and press OK to repair them." : !Readable ? (Settings.Enabled ? "Padmaster AI Assistant is enabled, but its settings could not be read. Try again shortly." : "Padmaster AI Assistant settings could not be read. Try again shortly.") : Listed ? "Padmaster AI Assistant is disabled in the Plugin Manager" : "Enable Padmaster AI Assistant in Tools > Manage > Plugins";

    public static ActivationState Parse(string? xml, Settings settings)
    {
        if (string.IsNullOrWhiteSpace(xml)) return new(settings, false, false);
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            if (document.Root?.Name.LocalName is not ("LaunchBox" or "Settings")) return new(settings, false, false);
            var elements = document.Descendants().Where(x => x.Name.LocalName == "DisabledPluginIds").ToArray();
            if (elements.Length > 1) return new(settings, false, false);
            var ids = (elements.FirstOrDefault()?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new(settings, ids.Any(x => Guid.TryParse(x, out var id) && id == Guid.Parse(PackageIdentity.Id)), true);
        }
        catch (XmlException) { return new(settings, false, false); }
    }

    public static ActivationState Read(string root, Action<string> log) => new ActivationReader(Path.Combine(root, "Data", "Settings.xml"), PackageIdentity.StorePath(root), log).Read();
}

public sealed class ActivationReader(string hostFile, string storeFile, Action<string> log)
{
    (long length, DateTime time)? _hostStamp, _storeStamp;
    bool _hostCached, _storeCached, _listed;
    Settings _settings = new();
    readonly HashSet<string> _errors = [];
    public int HostParses { get; private set; }
    public int StoreParses { get; private set; }
    static (long, DateTime)? Stamp(string path)
    {
        try { var info = new FileInfo(path); return (info.Length, info.LastWriteTimeUtc); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    public ActivationState Read()
    {
        try
        {
            var storeStamp = Stamp(storeFile);
            if (storeStamp == null && _storeCached && _storeStamp != null) throw new FileNotFoundException("Assistant settings file is temporarily unavailable.");
            if (!_storeCached || storeStamp != _storeStamp)
            {
                var settings = storeStamp == null ? new Settings() : Settings.ReadRequired(storeFile);
                _settings = settings; _storeStamp = storeStamp; _storeCached = true; StoreParses++;
            }
            var hostStamp = Stamp(hostFile);
            if (!_hostCached || hostStamp != _hostStamp)
            {
                var listed = false;
                if (hostStamp != null)
                {
                    using var stream = new FileStream(hostFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var parsed = ActivationState.Parse(reader.ReadToEnd(), _settings);
                    HostParses++;
                    if (!parsed.Readable) throw new InvalidDataException("LaunchBox settings are incomplete or invalid.");
                    listed = parsed.Listed;
                }
                _listed = listed; _hostStamp = hostStamp; _hostCached = true;
            }
            return new(_settings, _listed, true);
        }
        catch (Exception e)
        {
            var message = "Activation read unavailable; keeping the last known state: " + e.GetType().Name;
            if (_errors.Add(message)) log(message);
            return new(_settings, _listed, false, e is JsonException);
        }
    }
}

public sealed class ActivationGate(Action<Settings, bool> start, Action<Settings, Settings> change, Action stop, Action<string> notice)
{
    ActivationState _state = new(new(), false, false);
    bool _observed;
    public bool Active => _state.Active;
    public static bool OpenAtActivation(Settings settings, bool becomingActive) => settings.AutoOpen;   // not even the first Enable opens it (the owner 11/09)
    public void Update(ActivationState next)
    {
        if (!next.Readable) { if (!_observed) _state = next; return; }
        var previous = _state; var becomingActive = _observed && !previous.Settings.Enabled && next.Settings.Enabled;
        _state = next; _observed = true;
        if (next.Active && !previous.Active) start(next.Settings, becomingActive);
        else if (!next.Active && previous.Active) stop();
        else if (next.Active && next.Settings != previous.Settings) change(previous.Settings, next.Settings);
    }
    public void Open(Action open) { if (Active) open(); else notice(_state.Notice); }
}

public sealed class AssistantLifecycle(Action<Settings> listener, Action panel, Action<Settings> load, Action setup, Action poll, Action<bool> place, Action configure, Action stop)
{
    public void Start(Settings settings, bool becomingActive)
    {
        listener(settings);
        if (ActivationGate.OpenAtActivation(settings, becomingActive)) Open(settings, becomingActive, true);
    }
    public void Open(Settings settings, bool showSetup, bool startup = false)
    {
        panel(); load(settings);
        if (showSetup) setup();
        poll(); place(startup); configure();
    }
    public void Saved(Settings previous, Settings next, bool shown, Action<string> log)
    {
        // Pad Master works with the assistant in the background; after the first Enable the panel shows itself only when the user asked for that.
        if (!shown && next.Revision != previous.Revision && next.AutoOpen) { log($"settings saved; showing the assistant ({next.Placement})"); Open(next, false); }
        else if (!shown && next.Revision != previous.Revision) log("settings saved; panel stays hidden (open it from Tools)");
        else if (shown && previous.Placement != next.Placement) place(false);
    }
    public void Stop() => stop();
}
