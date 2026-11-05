using System.IO;
using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PadMaster;

public record Tool(string Name, string Description, JsonNode Schema, Func<JsonNode?, JsonNode> Handler);

public class Mcp
{
    readonly HttpListener _l = new();
    readonly IReadOnlyList<Tool> _tools;

    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public Mcp(int port, string path, IReadOnlyList<Tool> tools)
    {
        _tools = tools;
        _l.Prefixes.Add($"http://127.0.0.1:{port}{path.TrimEnd('/')}/");
    }

    public void Start()
    {
        _l.Start();
        new Thread(Loop) { IsBackground = true, Name = "mcp" }.Start();
    }

    public void Stop()
    {
        try { _l.Stop(); } catch { }
    }

    void Loop()
    {
        while (_l.IsListening)
        {
            HttpListenerContext c;
            try { c = _l.GetContext(); } catch { return; }
            try { Handle(c); }
            catch { try { c.Response.StatusCode = 500; c.Response.Close(); } catch { } }
        }
    }

    void Handle(HttpListenerContext c)
    {
        var res = c.Response;
        if (c.Request.Headers["Authorization"] != "Bearer " + Token) { res.StatusCode = 401; res.Close(); return; }
        if (c.Request.HttpMethod != "POST") { res.StatusCode = 405; res.Close(); return; }
        if (!string.Equals(c.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase)) { res.StatusCode = 415; res.Close(); return; }
        JsonObject? msg;
        using (var r = new StreamReader(c.Request.InputStream, Encoding.UTF8))
            try { msg = JsonNode.Parse(r.ReadToEnd()) as JsonObject; } catch { msg = null; }
        var id = msg?["id"];
        var method = msg?["method"]?.GetValue<string>() ?? "";
        JsonNode reply;
        if (msg == null) reply = Err(null, -32700, "parse error");
        else if (id == null || method.StartsWith("notifications/")) { res.StatusCode = 202; res.Close(); return; }
        else
        {
            try { reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = Rpc(method, msg["params"]) }; }
            catch (RpcError e) { reply = Err(id.DeepClone(), e.Code, e.Message); }
        }
        var bytes = Encoding.UTF8.GetBytes(reply.ToJsonString());
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    JsonNode Rpc(string method, JsonNode? p) => method switch
    {
        "initialize" => new JsonObject
        {
            ["protocolVersion"] = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "launchbox", ["version"] = "b1" },
        },
        "ping" => new JsonObject(),
        "tools/list" => new JsonObject
        {
            ["tools"] = new JsonArray(_tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
            }).ToArray()),
        },
        "tools/call" => Call(p),
        _ => throw new RpcError(-32601, $"method not found: {method}"),
    };

    JsonNode Call(JsonNode? p)
    {
        var name = p?["name"]?.GetValue<string>();
        var tool = _tools.FirstOrDefault(t => t.Name == name) ?? throw new RpcError(-32602, $"unknown tool: {name}");
        string text; var err = false;
        try { text = tool.Handler(p?["arguments"]).ToJsonString(); }
        catch (Exception e) { text = e.Message; err = true; }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = err,
        };
    }

    static JsonObject Err(JsonNode? id, int code, string msg) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = msg },
    };

    class RpcError(int code, string msg) : Exception(msg) { public int Code = code; }
}

public static class LocalImage
{
    public static void Validate(string path, Func<string, bool> exists)
    {
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/') || path[3..].Contains(':')) throw new ArgumentException("Choose an image on a local drive.");
        if (new DriveInfo(path[..3]).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram)) throw new ArgumentException("Choose an image on a local drive.");
        if (!exists(path)) throw new FileNotFoundException("Image not found", path);
    }
}
