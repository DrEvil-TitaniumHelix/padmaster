using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace PadMaster;

public sealed class Api
{
    public const int MaxBytes = 78000;
    public const string NoDelete = "Deleting is not possible through the assistant. Remove items yourself in LaunchBox.";
    static readonly HashSet<string> PlaylistMembership = new(StringComparer.OrdinalIgnoreCase) { "TryRemovePlaylistGame", "ClearGames" };
    static readonly Type[] RootTypes = [typeof(IDataManager), typeof(IStateManager), typeof(ILaunchBoxMainViewModel), typeof(IBigBoxMainViewModel), .. ApiServices.Types];
    static readonly Lazy<Dictionary<Type, Shape>> Shapes = new(BuildShapes);
    internal static bool ShapesLoaded => Shapes.IsValueCreated;
    static readonly ConcurrentDictionary<string, Documentation> Documents = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> Lists = new(StringComparer.OrdinalIgnoreCase) { ["games"] = "GetAllGames", ["platforms"] = "GetAllPlatforms", ["emulators"] = "GetAllEmulators", ["playlists"] = "GetAllPlaylists", ["platformCategories"] = "GetAllPlatformCategories", ["gameControllers"] = "GetGameControllers" };
    readonly IDataManager _data;
    readonly Dictionary<Type, Func<object?>> _roots;
    readonly Func<Func<object?>, object?> _dispatch;
    readonly Action<string> _log;
    readonly Lazy<Documentation> _documentation;
    Documentation _docs => _documentation.Value;
    readonly BulkChanges _bulk;
    public bool DocumentationLoaded => _documentation.IsValueCreated;
    readonly ConcurrentDictionary<string, (object value, Type type, string? key, JsonNode? parent)> _handles = new();
    readonly ConcurrentQueue<string> _handleOrder = new();
    long _sequence;

    public Api(IDataManager data, string xmlPath, IReadOnlyDictionary<Type, Func<object?>>? roots = null, Func<Func<object?>, object?>? dispatch = null, Action<string>? log = null, BulkChanges? bulk = null)
    {
        _data = data; _roots = roots?.ToDictionary(x => x.Key, x => x.Value) ?? []; _roots[typeof(IDataManager)] = () => _data;
        _dispatch = dispatch ?? Dispatch; _log = log ?? (_ => { }); _documentation = new(() => Documents.GetOrAdd(xmlPath, path => new Documentation(path)), LazyThreadSafetyMode.ExecutionAndPublication);
        _bulk = bulk ?? new(() => new Settings(), _ => false, (_, _) => { });
    }

    public static Api Host(string bridgeDirectory, Action<string> log, BulkChanges? bulk = null)
    {
        var roots = new Dictionary<Type, Func<object?>> { [typeof(IStateManager)] = () => PluginHelper.StateManager, [typeof(ILaunchBoxMainViewModel)] = () => PluginHelper.LaunchBoxMainViewModel, [typeof(IBigBoxMainViewModel)] = () => PluginHelper.BigBoxMainViewModel };
        foreach (var type in ApiServices.Types) roots[type] = () => ApiServices.Instances.GetValueOrDefault(type);
        return new(PluginHelper.DataManager, Path.Combine(PackageIdentity.RootFromBridge(bridgeDirectory), "Core", "Unbroken.LaunchBox.Plugins.xml"), roots, log: log, bulk: bulk);
    }

    public IReadOnlyList<Tool> Tools() =>
    [
        Make("lb_catalog", "Discover LaunchBox API types, exact member signatures, summaries and service availability. Use type and search to narrow results.", Schema(("type", "string", false), ("search", "string", false)), Catalog),
        Make("lb_list", "Read library rows. Case-insensitive where fields support contains/eq/gt/lt. Follow nextOffset until null; limit 1-2000 and total result size are capped.", Schema(("root", "string", true), ("where", "object", false), ("fields", "strings", false), ("limit", "integer", false), ("offset", "integer", false)), List),
        Make("lb_get", "Read properties of a library object or root. Children use parent {type,id} and their natural key. Returned @ handles address other API results for this session.", Schema(("type", "string", true), ("id", "string", false), ("parent", "object", false), ("fields", "strings", false)), Get),
        Make("lb_set", "WRITES to the LaunchBox library. Set all requested values in one save. Children require parent {type,id}. Discover writable fields with lb_catalog.", Schema(("type", "string", true), ("id", "string", false), ("parent", "object", false), ("values", "object", true)), a => Set(a)),
        Make("lb_call", "WRITES to the LaunchBox library. Invoke a public method exactly once; catalog mutates=false identifies read methods. Use args in signature order, API references as IDs or {type,id,parent}; optional cancellation tokens are automatic. For overloads use the catalog signature. A still running result must never be retried.", Schema(("type", "string", false), ("id", "string", false), ("parent", "object", false), ("method", "string", true), ("args", "array", false)), a => Call(a)),
        Make("lb_create", "WRITES to the LaunchBox library. Create an API object through its AddNew factory. Supply values, or items:[{values:{...}},...] for a batch under one parent, one save and one refresh. Natural keys are required for keyless children.", Schema(("type", "string", true), ("parent", "object", false), ("values", "object", false), ("items", "array", false)), a => Create(a))
    ];

    Tool Make(string name, string description, JsonObject schema, Func<JsonNode, JsonNode> handler) => new(name, description, schema, args => Execute(() =>
    {
        if (args is JsonObject obj && obj.ContainsKey("token")) throw new ArgumentException("Only the panel buttons can answer a held change.");
        return handler(args?.DeepClone() ?? new JsonObject());
    }));

    JsonNode Execute(Func<JsonNode> action)
    {
        try { return Cap(action()); }
        catch (Running running) { Observe(running); return new JsonObject { ["status"] = "still running", ["message"] = "The operation is still running. Do not repeat it." }; }
        catch (Exception error) { throw new ArgumentException(Short(Unwrap(error).Message, 6000)); }
    }

    public static JsonObject Schema(params (string name, string type, bool required)[] fields)
    {
        var properties = new JsonObject(); var required = new JsonArray();
        foreach (var (name, type, needed) in fields)
        {
            properties[name] = type == "strings" ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } : new JsonObject { ["type"] = type };
            if (needed) required.Add(name);
        }
        return new() { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    JsonNode Catalog(JsonNode args)
    {
        var search = Text(args, "search") ?? ""; var typeName = Text(args, "type");
        bool Matches(string name, string summary) => name.Contains(search, StringComparison.OrdinalIgnoreCase) || summary.Contains(search, StringComparison.OrdinalIgnoreCase);
        var result = new JsonObject { ["documentation"] = _docs.Status };
        if (typeName == null)
        {
            result["types"] = new JsonArray(Shapes.Value.Values.OrderBy(s => s.Type.Name).Where(s => Matches(s.Type.Name, _docs.Summary(s.Type))).Select(s => (JsonNode)new JsonObject { ["type"] = Name(s.Type), ["kind"] = s.Type.IsInterface ? "interface" : s.Type.IsEnum ? "enum" : "model", ["summary"] = Short(_docs.Summary(s.Type), 180), ["root"] = string.Join(",", s.Roots.Order()), ["members"] = s.Properties.Length + s.Methods.Count(m => !Locked(m)), ["key"] = s.Key?.Name }).ToArray());
            result["services"] = ServiceCatalog();
        }
        else
        {
            var unavailable = ServiceCatalog().OfType<JsonObject>().FirstOrDefault(s => Same(s["type"]!.GetValue<string>(), typeName) && s["exposure"]!.GetValue<string>() != "exposed");
            if (unavailable != null) { result["service"] = unavailable.DeepClone(); return result; }
            var shape = ShapeOf(TypeOf(typeName)); result["type"] = Name(shape.Type); result["summary"] = _docs.Summary(shape.Type); result["key"] = string.Join(" + ", shape.Keys.Select(p => p.Name)); result["parentRequired"] = shape.Child;
            result["addressing"] = shape.Keys.Length > 1 ? "Composite id is a JSON array of key values in the listed order; use the returned $handle." : shape.Keys.Length == 0 ? "Use a returned $handle for this session." : "Use the key value as id.";
            result["properties"] = new JsonArray(shape.Properties.Where(p => Matches(p.Name, _docs.Summary(p.Info))).Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = Name(p.Info.PropertyType), ["get"] = p.Info.CanRead, ["set"] = p.Info.CanWrite, ["summary"] = _docs.Summary(p.Info) }).ToArray());
            result["methods"] = new JsonArray(shape.Methods.Where(m => !Locked(m) && Matches(m.Name, _docs.Summary(m))).Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["signature"] = MemberKey(m), ["returns"] = Name(m.ReturnType), ["mutates"] = Mutates(m.Name), ["summary"] = _docs.Summary(m), ["params"] = new JsonArray(m.GetParameters().Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["type"] = Name(p.ParameterType), ["optional"] = p.IsOptional || p.ParameterType == typeof(CancellationToken) || p.IsDefined(typeof(ParamArrayAttribute)), ["summary"] = _docs.Parameter(m, p.Name!) }).ToArray()) }).ToArray());
            if (shape.Type.IsEnum) result["values"] = new JsonArray(Enum.GetNames(shape.Type).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        }
        return result;
    }

    JsonArray ServiceCatalog()
    {
        var rows = new JsonArray();
        foreach (var type in ApiServices.Types) rows.Add(new JsonObject { ["type"] = type.Name, ["manifestless"] = "Allow", ["exposure"] = "exposed", ["available"] = _roots.TryGetValue(type, out var root) && root() != null });
        rows.Add(new JsonObject { ["type"] = "IPluginPaths", ["manifestless"] = "Allow", ["exposure"] = "not exposed", ["reason"] = "Obsolete; IPluginFileSystemStore exposes the supported plugin file root." });
        rows.Add(new JsonObject { ["type"] = "IPluginSecretStore", ["manifestless"] = "Allow", ["exposure"] = "not exposed", ["reason"] = "Protected secrets are excluded from model tools." });
        rows.Add(new JsonObject { ["type"] = "IPluginCredentialStore", ["manifestless"] = "Deny", ["exposure"] = "not exposed", ["reason"] = "Not available to this plugin; credentials are excluded from model tools." });
        rows.Add(new JsonObject { ["type"] = "IFirstPartyHttpClientFactory", ["manifestless"] = "Deny", ["exposure"] = "not exposed", ["reason"] = "Not available to this plugin; requires first-party trust." });
        return rows;
    }

    JsonNode List(JsonNode args)
    {
        var root = Required(args, "root");
        if (!Lists.TryGetValue(root, out var method)) throw Unknown(root, Lists.Keys);
        var info = ShapeOf(typeof(IDataManager)).Methods.Single(m => m.Name == method); var type = Element(info.ReturnType)!; var shape = ShapeOf(type);
        var fields = Fields(shape, args["fields"], true); var filters = new List<Func<object, bool>>(); var filterErrors = 0;
        if (args["where"] is JsonObject where)
            foreach (var entry in where)
            {
                var property = Property(shape, entry.Key, false); var rules = entry.Value is JsonObject operations ? operations.ToArray() : [new KeyValuePair<string, JsonNode?>("eq", entry.Value)];
                foreach (var rule in rules)
                {
                    if (rule.Key is not ("contains" or "eq" or "gt" or "lt")) throw new ArgumentException("Unknown comparison: " + rule.Key);
                    var expected = rule.Key == "contains" ? rule.Value?.GetValue<string>() : ConvertValue(rule.Value, property.Info.PropertyType);
                    filters.Add(item => { try { return Compare(Read(property, item), expected, rule.Key); } catch (Running) { throw; } catch (Exception) { filterErrors++; return false; } });
                }
            }
        var offset = Math.Max(0, args["offset"]?.GetValue<int>() ?? 0); var limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 500, 1, 2000);
        var rows = new JsonArray(); var total = 0; var full = false; var size = 1024;
        foreach (var item in (IEnumerable)Invoke(info, _data, [])!)
        {
            if (!filters.All(filter => filter(item))) continue;
            if (total++ < offset || full) continue;
            if (rows.Count >= limit) { full = true; continue; }
            var row = Row(item, shape, fields); var bytes = Size(row);
            if (size + bytes > MaxBytes) { full = true; continue; }
            size += bytes; rows.Add(row);
        }
        var more = total > offset + rows.Count;
        var result = new JsonObject { ["rows"] = rows, ["total"] = total, ["nextOffset"] = more ? offset + rows.Count : null, ["truncated"] = more };
        if (more && rows.Count == 0) result["message"] = "One row exceeds the result cap. Request fewer fields before continuing.";
        if (filterErrors > 0) { result["filterErrors"] = filterErrors; result["message"] = "Some rows could not be filtered. Read without where to inspect per-field errors."; }
        return result;
    }

    JsonNode Get(JsonNode args)
    {
        var type = TypeOf(Required(args, "type")); var item = Resolve(type, Text(args, "id"), args["parent"]);
        return Row(item, ShapeOf(type), Fields(ShapeOf(type), args["fields"], false), args["parent"]);
    }

    JsonNode Set(JsonNode args, bool approved = false)
    {
        var type = TypeOf(Required(args, "type")); var shape = ShapeOf(type); var id = Text(args, "id"); var parent = args["parent"];
        if (id != null && _handles.TryGetValue(id, out var handle) && handle.key != null && handle.type == type) { id = handle.key; parent ??= handle.parent; }
        var values = Prepare(shape, args["values"] as JsonObject ?? throw new ArgumentException("values required"));
        _ = Resolve(type, id, parent); JsonNode? result = null;
        if (!approved) return _bulk.Run("change", type.Name, 1, () => Execute(() => Set(args, true)));
        SaveChanges(() =>
        {
            var item = TryResolve(type, id, parent);
            if (item == null && id != null && shape.Keys.Length > 0)
            {
                var parts = shape.Keys.Length == 1 ? [id] : JsonSerializer.Deserialize<string[]>(id)!;
                for (var i = 0; i < shape.Keys.Length; i++) if (values.FirstOrDefault(v => v.property == shape.Keys[i]) is var replacement && replacement.property != null) parts[i] = ScalarText(replacement.value);
                item = TryResolve(type, shape.Keys.Length == 1 ? parts[0] : JsonSerializer.Serialize(parts), parent);
            }
            item ??= Resolve(type, id, parent); Apply(item, values);
            result ??= Row(item, shape, values.Select(v => v.property).ToArray(), parent);
        });
        return result ?? new JsonObject { ["status"] = "save not applied" };
    }

    JsonNode Create(JsonNode args)
    {
        var type = TypeOf(Required(args, "type")); var shape = ShapeOf(type); var parent = args["parent"];
        var ownerType = parent == null ? typeof(IDataManager) : TypeOf(Required(parent, "type"));
        object Owner() => parent == null ? _data : Resolve(ownerType, Text(parent, "id"), parent["parent"]);
        var factory = ShapeOf(ownerType).Methods.SingleOrDefault(m => m.Name.StartsWith("AddNew", StringComparison.Ordinal) && m.ReturnType == type) ?? throw new ArgumentException("No AddNew factory for " + Name(type) + " on " + Name(ownerType) + "; discover alternatives with lb_catalog/lb_call.");
        if (args["items"] != null && args["values"] != null) throw new ArgumentException("Use values or items, not both.");
        var input = args["items"] is JsonArray batch ? batch.Select(item => item?["values"] as JsonObject ?? throw new ArgumentException("Each item requires values.")).ToArray() : [args["values"] as JsonObject ?? throw new ArgumentException("values or items required")];
        if (input.Length is < 1 or > 2000) throw new ArgumentException("Use 1-2000 items per create.");
        var sets = input.Select(values => Prepare(shape, values, factory)).ToArray();
        var factoryArgs = input.Select(values => factory.GetParameters().Select(p => Find(values, p.Name!) is { } value ? ConvertValue(value, p.ParameterType) : p.HasDefaultValue ? p.DefaultValue : throw new ArgumentException("values must include factory argument " + p.Name)).ToArray()).ToArray();
        if (shape.Keys.Length == 0) throw new ArgumentException("No stable create key for " + Name(type) + "; use lb_call for its factory.");
        if (shape.Child && shape.Keys.Any(key => key.Info.CanWrite && input.Any(values => Find(values, key.Name) == null))) throw new ArgumentException("values must include natural key " + string.Join(", ", shape.Keys.Select(key => key.Name)));
        _ = Owner(); var keys = new string?[input.Length]; var natural = input.Select(values => shape.Keys.Length > 1 ? JsonSerializer.Serialize(shape.Keys.Select(key => Find(values, key.Name)?.ToString() ?? "")) : Find(values, shape.Keys[0].Name)?.ToString() ?? Find(values, "Name")?.ToString()).ToArray();
        var output = new JsonArray(); var first = true; var created = 0;
        SaveChanges(() =>
        {
            var owner = Owner();
            for (var i = 0; i < input.Length; i++)
            {
                var item = keys[i] == null ? null : FindObject(type, keys[i]!, owner, ownerType);
                if (item == null && natural[i] != null && (!first || shape.Child)) item = FindNatural(type, natural[i]!, owner, ownerType);
                if (item == null) { item = Invoke(factory, owner, factoryArgs[i]) ?? throw new InvalidOperationException("Factory returned null."); if (first) created++; }
                Apply(item, sets[i]); keys[i] = KeyOf(shape, item);
                if (first) output.Add(Row(item, shape, Fields(shape, null, true).Concat(sets[i].Select(v => v.property)).DistinctBy(p => p.Name).ToArray(), parent));
            }
            first = false;
        });
        return new JsonObject { ["created"] = created, ["rows"] = output };
    }

    JsonNode Call(JsonNode args, bool approved = false)
    {
        var method = Required(args, "method"); var typeName = Text(args, "type"); var types = typeName == null ? RootTypes.Where(t => _roots.ContainsKey(t)) : [TypeOf(typeName)];
        var matches = new List<(Type type, MethodInfo method, object?[] args)>(); var errors = new List<string>();
        foreach (var type in types)
            foreach (var candidate in ShapeOf(type).Methods.Where(m => Same(m.Name, method) || Same(MemberKey(m), method)))
            {
                if (Locked(candidate)) return new JsonObject { ["refused"] = true, ["message"] = NoDelete };
                try { matches.Add((type, candidate, Arguments(candidate, args["args"] as JsonArray ?? []))); } catch (Exception e) { errors.Add(Unwrap(e).Message); }
            }
        if (matches.Count != 1) throw new ArgumentException(matches.Count > 1 ? "Ambiguous method; specify type and the full catalog signature." : "No matching method: " + method + ". " + string.Join("; ", errors.Take(3)));
        var match = matches[0]; var item = Resolve(match.type, Text(args, "id"), args["parent"]);
        if (!approved && Mutates(match.method.Name) && !match.method.Name.StartsWith("AddNew", StringComparison.Ordinal))
        {
            var verb = match.method.Name.StartsWith("Clear", StringComparison.OrdinalIgnoreCase) ? "clear" : Removing(match.method.Name) ? "remove" : "change";
            var affectedType = match.type == typeof(IPlaylist) && match.method.Name == "ClearGames" ? "IPlaylistGame" : match.type.Name;
            return _bulk.Run(verb, affectedType, EffectCount(match.method, item, match.args), () => Execute(() => Call(args, true)));
        }
        var returned = Invoke(match.method, item, match.args, NeedsSave(match.method) ? () => { _data.Save(false); Refresh(); } : null);
        return Serialize(returned, ResultType(match.method.ReturnType), 0, args["id"] == null ? null : Reference(match.type, Text(args, "id"), args["parent"])) ?? new JsonObject { ["ok"] = true };
    }

    static bool Removing(string name) => new[] { "Remove", "TryRemove", "Delete", "TryDelete" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    static bool Locked(MethodInfo method) => (Removing(method.Name) || method.Name.StartsWith("Clear", StringComparison.OrdinalIgnoreCase)) && !(method.DeclaringType == typeof(IPlaylist) && PlaylistMembership.Contains(method.Name));
    int? EffectCount(MethodInfo method, object item, object?[] args)
    {
        if (method.Name.StartsWith("Clear", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = method.DeclaringType == typeof(IPlaylist) && method.Name == "ClearGames" ? "PlaylistGames" : method.Name[5..];
            var reader = ShapeOf(method.DeclaringType!).Methods.FirstOrDefault(m => m.Name == "GetAll" + suffix && m.GetParameters().Length == 0 && Element(m.ReturnType) != null);
            if (reader == null) return null;
            try { return Invoke(reader, item, []) is IEnumerable rows ? rows.Cast<object?>().Count() : null; } catch { return null; }
        }
        var collections = args.OfType<IEnumerable>().Where(a => a is not string).ToArray();
        if (collections.Length > 0) return collections.Sum(a => a.Cast<object?>().Count());
        if (Removing(method.Name)) return args.Any(a => a != null && a is not CancellationToken) ? 1 : null;
        return 1;
    }

    void SaveChanges(Action changes)
    {
        var runs = 0;
        _data.BackgroundReloadSave(() =>
        {
            var run = Interlocked.Increment(ref runs);
            try { changes(); }
            catch (Exception e) when (run > 1) { if (e is Running running) Observe(running); _log("API background save: " + SafeDiagnostics.Error(Unwrap(e))); }
        });
        if (runs > 0) Refresh();
    }

    void Refresh()
    {
        try
        {
            if (_roots.TryGetValue(typeof(ILaunchBoxMainViewModel), out var root) && root() is ILaunchBoxMainViewModel view) _dispatch(() => { view.RefreshData(); return null; });
        }
        catch (Exception e) { if (e is Running running) Observe(running); _log("API refresh: " + SafeDiagnostics.Error(Unwrap(e))); }
    }

    static bool Ui(Type? type) => type == typeof(ILaunchBoxMainViewModel) || type == typeof(IBigBoxMainViewModel);
    object? Route(Type? type, Func<object?> action)
    {
        if (Ui(type)) return _dispatch(action);
        try { return action(); }
        catch (Exception e) when (Affinity(Unwrap(e))) { return _dispatch(action); }
    }
    static bool Affinity(Exception e) => e is InvalidOperationException && new System.Diagnostics.StackTrace(e).GetFrames().Any(f => f.GetMethod() is { DeclaringType.FullName: "System.Windows.Threading.Dispatcher" } method && (method.Name == "VerifyAccess" || method.Name.StartsWith("<VerifyAccess>", StringComparison.Ordinal)));
    object? Invoke(MethodInfo method, object item, object?[] args, Action? after = null)
    {
        object? Once()
        {
            var value = method.Invoke(item, args);
            if (value is Task task) return Finish(task, after);
            after?.Invoke(); return value;
        }
        if (Ui(method.DeclaringType)) return Complete(_dispatch(Once));
        object? result;
        try { result = method.Invoke(item, args); }
        catch (Exception error) when (Affinity(Unwrap(error))) { return Complete(_dispatch(Once)); }
        if (result is Task pending) return Complete(Finish(pending, after));
        after?.Invoke(); return result;
    }
    static async Task<object?> Finish(Task task, Action? after)
    {
        await task.ConfigureAwait(false); after?.Invoke();
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    static object? Complete(object? value)
    {
        if (value is not Task task) return value;
        try { task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); }
        catch (TimeoutException) { throw new Running(task); }
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }
    public static object? Dispatch(Func<object?> action) => DispatchOn(Application.Current?.Dispatcher ?? throw new InvalidOperationException("UI dispatcher unavailable."), action, TimeSpan.FromSeconds(30));
    public static object? DispatchOn(Dispatcher dispatcher, Func<object?> action, TimeSpan timeout)
    {
        if (dispatcher.CheckAccess()) return action();
        if (dispatcher.HasShutdownStarted) throw new InvalidOperationException("UI dispatcher is shutting down.");
        var task = dispatcher.InvokeAsync(action).Task;
        try { return task.WaitAsync(timeout).GetAwaiter().GetResult(); }
        catch (TimeoutException) { throw new Running(task); }
    }
    static bool Mutates(string name) => !new[] { "Get", "Is", "Has", "Find", "Can" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    static bool NeedsSave(MethodInfo method) => Mutates(method.Name) && !Ui(method.DeclaringType) && method.Name is not ("Save" or "ForceReload" or "ReloadIfNeeded" or "RefreshData" or "PlayGame" or "Play" or "Launch" or "OpenManual" or "OpenFolder" or "Close" or "Minimize" or "Maximize" or "Restore");

    object Resolve(Type type, string? id, JsonNode? parent) => TryResolve(type, id, parent) ?? throw new ArgumentException(Name(type) + " not found: " + id);
    object? TryResolve(Type type, string? id, JsonNode? parent)
    {
        if (id != null && id.StartsWith('@'))
        {
            if (!_handles.TryGetValue(id, out var stored) || !type.IsInstanceOfType(stored.value)) throw new ArgumentException("Expired or incompatible handle: " + id);
            if (stored.key != null && stored.type.Namespace == typeof(IGame).Namespace && (!ShapeOf(stored.type).Child || stored.parent != null)) return TryResolve(stored.type, stored.key, stored.parent);
            return stored.value;
        }
        if (_roots.TryGetValue(type, out var root)) return root() ?? throw new ArgumentException(Name(type) + " is not available in this host.");
        if (id == null) throw new ArgumentException("id required for " + Name(type));
        if (parent != null)
        {
            var ownerType = TypeOf(Required(parent, "type")); return FindObject(type, id, Resolve(ownerType, Text(parent, "id"), parent["parent"]), ownerType);
        }
        if (ShapeOf(type).Child) throw new ArgumentException("parent {type,id} required for " + Name(type));
        return FindObject(type, id, _data, typeof(IDataManager));
    }
    object? FindObject(Type type, string id, object owner, Type ownerType)
    {
        var methods = ShapeOf(ownerType).Methods;
        var lookup = methods.FirstOrDefault(m => m.ReturnType == type && m.Name.StartsWith("Get", StringComparison.Ordinal) && (m.Name.EndsWith("ById", StringComparison.Ordinal) || m.Name.EndsWith("ByName", StringComparison.Ordinal)) && m.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(string));
        if (lookup != null) return Invoke(lookup, owner, [id]);
        var shape = ShapeOf(type); if (shape.Keys.Length == 0) throw new ArgumentException("Use a returned @ handle for " + Name(type));
        return Unique(Children(type, owner, ownerType).Where(item => Same(KeyOf(shape, item), id)), type, id);
    }
    object? FindNatural(Type type, string key, object owner, Type ownerType)
    {
        var shape = ShapeOf(type);
        if (shape.Child && shape.Keys.Length > 1) return Unique(Children(type, owner, ownerType).Where(item => Same(KeyOf(shape, item), key)), type, key);
        var property = shape.Child ? shape.Key : shape.Properties.FirstOrDefault(p => p.Name == "Name");
        return property == null ? null : Unique(Children(type, owner, ownerType).Where(item => Same(ScalarText(Read(property, item)), key)), type, key);
    }
    IEnumerable<object> Children(Type type, object owner, Type ownerType)
    {
        var shape = ShapeOf(ownerType); var method = shape.Methods.FirstOrDefault(m => m.Name.StartsWith("Get", StringComparison.Ordinal) && Element(m.ReturnType) == type && m.GetParameters().Length == 0);
        if (method != null) return ((IEnumerable?)Invoke(method, owner, []))?.Cast<object>() ?? [];
        var property = shape.Properties.FirstOrDefault(p => Element(p.Info.PropertyType) == type && p.Info.CanRead);
        if (property != null) return ((IEnumerable?)Read(property, owner))?.Cast<object>() ?? [];
        throw new ArgumentException(Name(ownerType) + " has no parameterless collection of " + Name(type) + "; use lb_call and returned handles.");
    }
    static object? Unique(IEnumerable<object> values, Type type, string id)
    {
        var matches = values.Take(2).ToArray(); return matches.Length > 1 ? throw new ArgumentException("Ambiguous " + Name(type) + " key " + id) : matches.FirstOrDefault();
    }

    (Field property, object? value)[] Prepare(Shape shape, JsonObject values, MethodInfo? factory = null)
    {
        if (values.Count == 0) throw new ArgumentException("values must contain at least one field.");
        return values.Select(pair => { var p = Property(shape, pair.Key, false); if (!p.Info.CanWrite && factory?.GetParameters().Any(parameter => Same(parameter.Name, pair.Key)) != true) throw new ArgumentException(p.Name + " is read-only"); return (p, ConvertValue(pair.Value, p.Info.PropertyType)); }).Where(pair => pair.p.Info.CanWrite).ToArray();
    }
    void Apply(object item, (Field property, object? value)[] values)
    {
        foreach (var (property, value) in values) Route(property.Info.DeclaringType, () => { property.Info.SetValue(item, value); return null; });
    }
    object?[] Arguments(MethodBase method, JsonArray args)
    {
        var parameters = method.GetParameters(); if (args.Count > parameters.Length) throw new ArgumentException("Too many arguments for " + method.Name);
        return parameters.Select((p, i) => i < args.Count ? ConvertValue(args[i], p.ParameterType) : p.ParameterType == typeof(CancellationToken) ? CancellationToken.None : p.HasDefaultValue ? p.DefaultValue : p.IsDefined(typeof(ParamArrayAttribute)) ? Array.CreateInstance(p.ParameterType.GetElementType()!, 0) : throw new ArgumentException("Missing argument " + p.Name)).ToArray();
    }
    object? ConvertValue(JsonNode? value, Type type)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (type == typeof(CancellationToken)) return CancellationToken.None;
        if (value == null) return !type.IsValueType || Nullable.GetUnderlyingType(type) != null ? null : throw new ArgumentException("null is not valid for " + Name(type));
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return value.GetValue<string>();
        if (type.IsEnum) return Enum.Parse(type, value.ToString(), true);
        if (type == typeof(Guid)) return Guid.Parse(value.GetValue<string>());
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture);
        if (type == typeof(DateTime)) return DateTime.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture);
        if (typeof(Delegate).IsAssignableFrom(type)) throw new ArgumentException("Executable callback parameters cannot be supplied as JSON.");
        if (type.IsInterface && type.Assembly == typeof(IDataManager).Assembly)
        {
            var referenceType = value is JsonObject && Text(value, "type") != null ? TypeOf(Required(value, "type")) : type;
            if (!type.IsAssignableFrom(referenceType)) throw new ArgumentException("Incompatible API reference.");
            return Resolve(referenceType, value is JsonObject ? Text(value, "id") : value.GetValue<string>(), value is JsonObject ? value["parent"] : null);
        }
        var element = Element(type);
        if (element != null && value is JsonArray array)
        {
            var converted = Array.CreateInstance(element, array.Count); for (var i = 0; i < array.Count; i++) converted.SetValue(ConvertValue(array[i], element), i);
            if (type.IsAssignableFrom(converted.GetType())) return converted;
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!; foreach (var item in converted) list.Add(item);
            if (type.IsAssignableFrom(list.GetType())) return list;
        }
        if (type.Assembly == typeof(IDataManager).Assembly && value is JsonObject fields)
        {
            var constructors = type.GetConstructors().Where(c => c.GetParameters().All(p => Find(fields, p.Name!) != null || p.HasDefaultValue)).OrderByDescending(c => c.GetParameters().Length).ToArray();
            foreach (var constructor in constructors)
                try
                {
                    var result = constructor.Invoke(constructor.GetParameters().Select(p => Find(fields, p.Name!) is { } field ? ConvertValue(field, p.ParameterType) : p.DefaultValue).ToArray());
                    foreach (var pair in fields)
                    {
                        var property = ShapeOf(type).Properties.FirstOrDefault(p => Same(p.Name, pair.Key));
                        if (property?.Info.CanWrite == true) property.Info.SetValue(result, ConvertValue(pair.Value, property.Info.PropertyType));
                        else if (!constructor.GetParameters().Any(p => Same(p.Name, pair.Key))) throw Unknown(pair.Key, ShapeOf(type).Properties.Select(p => p.Name));
                    }
                    return result;
                }
                catch (Exception) { }
            throw new ArgumentException("Cannot construct " + Name(type) + " from supplied fields; inspect lb_catalog.");
        }
        return JsonSerializer.Deserialize(value.ToJsonString(), type, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    JsonObject Row(object item, Shape shape, Field[] fields, JsonNode? parent = null, int depth = 0)
    {
        var row = new JsonObject();
        foreach (var field in fields)
            try { row[field.Name] = Serialize(Read(field, item), field.Info.PropertyType, depth + 1, parent); }
            catch (Running) { throw; }
            catch (Exception e) { row[field.Name] = "<error: " + Short(Unwrap(e).Message, 300) + ">"; }
        return row;
    }
    object? Read(Field field, object item) => Route(field.Info.DeclaringType, () => field.Getter(item));
    JsonNode? Serialize(object? value, Type? declared = null, int depth = 0, JsonNode? parent = null)
    {
        if (value == null) return null;
        var type = value.GetType();
        if (value is string text) return JsonValue.Create(Short(text, 20000));
        if (type.IsEnum || value is Guid or TimeSpan or DateTimeOffset) return JsonValue.Create(ScalarText(value));
        if (type.IsPrimitive || value is decimal or DateTime) return JsonSerializer.SerializeToNode(value);
        if (value is System.Drawing.Image || value is System.Windows.Media.ImageSource) return JsonValue.Create("<image>");
        if (depth > 1) return JsonValue.Create("<" + Name(type) + ">");
        if (value is IEnumerable collection)
        {
            var array = new JsonArray(); var bytes = 256;
            foreach (var entry in collection)
            {
                var node = entry is IEnumerable and not string ? JsonValue.Create("<" + Name(entry.GetType()) + ">") : Serialize(entry, Element(declared ?? type), Math.Max(1, depth), parent); bytes += Size(node);
                if (bytes > MaxBytes - 2048 || array.Count >= 2000) { array.Add(new JsonObject { ["truncated"] = true }); break; }
                array.Add(node);
            }
            return array;
        }
        var apiType = value is IPlaylist ? typeof(IPlaylist) : value is IPlatformCategory ? typeof(IPlatformCategory) : declared != null && Shapes.Value.ContainsKey(declared) ? declared : type.GetInterfaces().FirstOrDefault(Shapes.Value.ContainsKey) ?? (Shapes.Value.ContainsKey(type) ? type : null);
        if (apiType == null) return JsonValue.Create("<" + Name(type) + ">");
        var shape = ShapeOf(apiType); var handle = "@" + Interlocked.Increment(ref _sequence); string? key = null;
        try { if (shape.Keys.Length > 0) key = KeyOf(shape, value); } catch (Exception) { }
        _handles[handle] = (value, apiType, key, parent?.DeepClone()); _handleOrder.Enqueue(handle);
        while (_handleOrder.Count > 2048 && _handleOrder.TryDequeue(out var expired)) _handles.TryRemove(expired, out _);
        var result = Row(value, shape, Fields(shape, null, depth > 0), parent, depth);
        result["$type"] = Name(apiType); result["$handle"] = handle;
        if (parent != null && shape.Child) result["$parent"] = parent.DeepClone();
        return result;
    }

    static Field[] Fields(Shape shape, JsonNode? fields, bool brief)
    {
        if (fields is JsonArray array) return array.Select(n => Property(shape, n?.GetValue<string>() ?? throw new ArgumentException("fields must be strings"), false)).DistinctBy(p => p.Name).ToArray();
        return shape.Properties.Where(p => p.Info.CanRead && (!brief || shape.Keys.Contains(p) || p.Name is "Title" or "Name" or "Platform" or "FilePath" or "ImageType")).ToArray();
    }
    static Field Property(Shape shape, string name, bool write)
    {
        if (!shape.ByName.TryGetValue(name, out var property)) throw Unknown(name, shape.ByName.Keys);
        if (write ? !property.Info.CanWrite : !property.Info.CanRead) throw new ArgumentException(name + (write ? " is read-only" : " is write-only"));
        return property;
    }
    static Shape ShapeOf(Type type) => Shapes.Value[type];
    static Type TypeOf(string name)
    {
        var types = Shapes.Value.Keys.Where(t => Same(Name(t), name) || Same(t.FullName, name) || Same(t.IsInterface ? t.Name[1..] : t.Name, name)).Take(2).ToArray();
        if (types.Length != 1) throw Unknown(name, Shapes.Value.Keys.Select(Name)); return types[0];
    }
    static Dictionary<Type, Shape> BuildShapes()
    {
        var shapes = new Dictionary<Type, Shape>(); var assembly = typeof(IDataManager).Assembly;
        foreach (var root in RootTypes)
        {
            var queue = new Queue<Type>(); var seen = new HashSet<Type>(); queue.Enqueue(root);
            while (queue.TryDequeue(out var type))
            {
                if (!seen.Add(type)) continue;
                if (type.HasElementType) { queue.Enqueue(type.GetElementType()!); continue; }
                if (type.IsGenericType) foreach (var arg in type.GetGenericArguments()) queue.Enqueue(arg);
                if (type.Assembly != assembly || !type.IsPublic || type.ContainsGenericParameters) continue;
                if (!shapes.TryGetValue(type, out var shape)) shapes[type] = shape = new Shape(type);
                shape.Roots.Add(root.Name);
                foreach (var inherited in type.GetInterfaces()) queue.Enqueue(inherited);
                foreach (var property in shape.Properties) queue.Enqueue(property.Info.PropertyType);
                foreach (var method in shape.Methods) { queue.Enqueue(method.ReturnType); foreach (var parameter in method.GetParameters()) queue.Enqueue(parameter.ParameterType); }
            }
        }
        return shapes;
    }
    sealed class Shape
    {
        public readonly Type Type;
        public readonly Field[] Properties;
        public readonly MethodInfo[] Methods;
        public readonly Dictionary<string, Field> ByName;
        public readonly HashSet<string> Roots = [];
        public readonly Field? Key;
        public readonly Field[] Keys;
        public readonly bool Child;
        public Shape(Type type)
        {
            Type = type; var bases = type.IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : [type];
            Properties = bases.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)).Where(p => p.GetIndexParameters().Length == 0).DistinctBy(p => p.Name).OrderBy(p => p.Name).Select(p => new Field(p)).ToArray();
            ByName = Properties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            Methods = bases.SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)).Where(m => !m.IsSpecialName && m.DeclaringType != typeof(object)).DistinctBy(MemberKey).OrderBy(m => m.Name).ToArray();
            var stem = type.IsInterface ? type.Name[1..] : type.Name;
            Key = Properties.FirstOrDefault(p => p.Name == "Id") ?? Properties.FirstOrDefault(p => p.Name == stem + "Id");
            var natural = type.Name switch { "IPlatform" or "IPlatformCategory" or "ICustomField" or "IAlternateName" => "Name", "IPlaylistGame" => "GameId", "IEmulatorPlatform" => "Platform", "IMount" => "DriveLetter", _ => null };
            Key ??= Properties.FirstOrDefault(p => p.Name == natural);
            var composite = type.Name switch { "IPlaylistFilter" => new[] { "FieldKey", "ComparisonTypeKey", "Value" }, "IParent" => ["PlatformName", "PlaylistId", "PlatformCategoryName", "ParentPlatformName", "ParentPlaylistId", "ParentPlatformCategoryName"], _ => [] };
            Keys = Key != null ? [Key] : composite.Select(name => ByName[name]).ToArray();
            Child = type.Name is "ICustomField" or "IAlternateName" or "IPlaylistGame" or "IEmulatorPlatform" or "IMount" or "IPlaylistFilter" or "IAdditionalApplication" or "IPlatformDocument" or "IPlatformFolder";
        }
    }
    sealed class Field
    {
        public readonly PropertyInfo Info;
        public string Name => Info.Name;
        public readonly Func<object, object?> Getter;
        public Field(PropertyInfo info)
        {
            Info = info; var obj = Expression.Parameter(typeof(object));
            Getter = info.CanRead ? Expression.Lambda<Func<object, object?>>(Expression.Convert(Expression.Property(Expression.Convert(obj, info.DeclaringType!), info), typeof(object)), obj).Compile() : _ => throw new ArgumentException("Property is write-only.");
        }
    }
    sealed class Documentation
    {
        readonly Dictionary<string, XElement> _members = [];
        public readonly string Status;
        public Documentation(string path)
        {
            try { _members = XDocument.Load(path).Descendants("member").Where(e => e.Attribute("name") != null).ToDictionary(e => e.Attribute("name")!.Value); Status = "LaunchBox Core XML documentation loaded"; }
            catch (Exception) { Status = "XML documentation unavailable; reflection names only"; }
        }
        public string Summary(MemberInfo member) => Content(_members.GetValueOrDefault(MemberKey(member))?.Element("summary"));
        public string Parameter(MethodInfo method, string name) => Content(_members.GetValueOrDefault(MemberKey(method))?.Elements("param").FirstOrDefault(p => p.Attribute("name")?.Value == name));
        static string Content(XElement? element) => element == null ? "" : Regex.Replace(string.Concat(element.Nodes().Select(Node)), @"\s+", " ").Trim();
        static string Node(XNode node) => node is XText text ? text.Value : node is XElement element ? element.Name.LocalName is "see" or "seealso" ? element.Attribute("cref")?.Value[2..] ?? element.Attribute("langword")?.Value ?? element.Value : element.Name.LocalName == "paramref" ? element.Attribute("name")?.Value ?? "" : Content(element) : "";
    }
    public static string MemberKey(MemberInfo member)
    {
        if (member is Type type) return "T:" + (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName;
        var declaring = member.DeclaringType!; if (declaring.IsGenericType) declaring = declaring.GetGenericTypeDefinition();
        var prefix = member is PropertyInfo ? "P:" : "M:"; var name = prefix + declaring.FullName + "." + member.Name;
        if (member is MethodInfo method)
        {
            if (method.IsGenericMethod) name += "``" + method.GetGenericArguments().Length;
            if (method.DeclaringType!.IsConstructedGenericType) method = declaring.GetMethods().First(m => m.MetadataToken == method.MetadataToken);
            var parameters = method.GetParameters(); if (parameters.Length > 0) name += "(" + string.Join(",", parameters.Select(p => XmlType(p.ParameterType))) + ")";
        }
        return name;
    }
    static string XmlType(Type type) => type.IsByRef ? XmlType(type.GetElementType()!) + "@" : type.IsArray ? XmlType(type.GetElementType()!) + (type.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]") : type.IsGenericParameter ? (type.DeclaringMethod == null ? "`" : "``") + type.GenericParameterPosition : type.IsGenericType ? type.GetGenericTypeDefinition().FullName!.Split('`')[0] + "{" + string.Join(",", type.GetGenericArguments().Select(XmlType)) + "}" : type.FullName!.Replace('+', '.');
    static Type ResultType(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>) ? type.GetGenericArguments()[0] : type;
    static Type? Element(Type type) => type.IsArray ? type.GetElementType() : type.IsGenericType && type.GetGenericArguments().Length == 1 && typeof(IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments()[0] : null;
    static string Name(Type type) => type.IsGenericType ? type.Name.Split('`')[0] + "<" + string.Join(",", type.GetGenericArguments().Select(Name)) + ">" : type.IsArray ? Name(type.GetElementType()!) + "[]" : type.Name;
    static bool Compare(object? actual, object? expected, string operation)
    {
        if (operation == "contains") return actual != null && ScalarText(actual).Contains(ScalarText(expected), StringComparison.OrdinalIgnoreCase);
        if (operation == "eq") return actual is string || expected is string ? Same(ScalarText(actual), ScalarText(expected)) : Equals(actual, expected);
        if (actual == null || expected == null) return false;
        var comparison = actual is IComparable comparable ? comparable.CompareTo(expected) : string.Compare(ScalarText(actual), ScalarText(expected), StringComparison.OrdinalIgnoreCase);
        return operation == "gt" ? comparison > 0 : comparison < 0;
    }
    static string ScalarText(object? value) => value is IFormattable formatted ? formatted.ToString(null, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
    string KeyOf(Shape shape, object item) => shape.Keys.Length == 1 ? ScalarText(Read(shape.Keys[0], item)) : JsonSerializer.Serialize(shape.Keys.Select(key => ScalarText(Read(key, item))));
    static JsonObject Reference(Type type, string? id, JsonNode? parent) => new() { ["type"] = Name(type), ["id"] = id, ["parent"] = parent?.DeepClone() };
    static JsonNode? Find(JsonObject node, string name) => node.FirstOrDefault(pair => Same(pair.Key, name)).Value;
    static string? Text(JsonNode node, string name) => node[name]?.GetValue<string>();
    static string Required(JsonNode node, string name) => Text(node, name) ?? throw new ArgumentException(name + " required");
    static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static Exception Unwrap(Exception error) => error is TargetInvocationException { InnerException: { } inner } ? Unwrap(inner) : error;
    static ArgumentException Unknown(string value, IEnumerable<string> names) => new("Unknown member '" + value + "'. Nearest names: " + string.Join(", ", names.OrderBy(n => Distance(value.ToLowerInvariant(), n.ToLowerInvariant())).ThenBy(n => n).Take(5)));
    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++) { var previous = row[0]; row[0] = i; for (var j = 1; j <= b.Length; j++) { var old = row[j]; row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), previous + (a[i - 1] == b[j - 1] ? 0 : 1)); previous = old; } }
        return row[^1];
    }
    static string Short(string text, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= bytes) return text;
        var length = Math.Min(text.Length, bytes - 3); while (length > 0 && (char.IsHighSurrogate(text[length - 1]) || Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > bytes - 3)) length--;
        return text[..length] + "...";
    }
    static int Size(JsonNode? node) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(node?.ToJsonString() ?? "null")) + 2;
    static JsonNode Cap(JsonNode node)
    {
        if (Size(node) <= MaxBytes) return node;
        var result = node as JsonObject ?? new JsonObject { ["result"] = node }; result["truncated"] = true;
        while (Size(result) > MaxBytes)
        {
            var largest = result.Where(p => p.Key != "truncated").OrderByDescending(p => Size(p.Value)).First();
            if (largest.Value is JsonArray array && array.Count > 0) array.RemoveAt(array.Count - 1);
            else if (largest.Value is JsonObject obj && obj.Count > 0) obj.Remove(obj.Last().Key);
            else result[largest.Key] = "<truncated; request fewer fields or narrow catalog search>";
        }
        return result;
    }
    void Observe(Running running)
    {
        if (running.Completion != null) _ = running.Completion.ContinueWith(task => _log("API delayed operation: " + SafeDiagnostics.Error(task.Exception!.GetBaseException())), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
    public sealed class Running(Task completion) : Exception("still running") { public Task Completion { get; } = completion; }
}
