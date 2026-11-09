using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using Unbroken.LaunchBox.Plugins.Data;

namespace PadMaster.Controls;

public sealed record ArtworkResult(IReadOnlyDictionary<string, string> Written, IReadOnlyDictionary<string, string> Retained, int Created, int Changed, bool ManifestChanged, bool RefreshPending)
{
    public int Writes => Created + Changed;
}

public static class Artwork
{
    static readonly object Gate = new();
    static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    sealed record Manifest(Dictionary<string, string> Images, bool RefreshPending);
    public static string Marker(string title, string controller, string state) => $"{title}|{controller}|{state}";
    static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var b in data) { crc ^= b; for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
        return ~crc;
    }
    static IEnumerable<(int Offset, int Length, string Type)> Chunks(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("Invalid PNG signature.");
        var pos = 8;
        while (pos <= png.Length - 12)
        {
            var n = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
            if (n > png.Length - pos - 12) throw new InvalidDataException("Invalid PNG chunk length.");
            var length = (int)n; var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (Crc(png.AsSpan(pos + 4, length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + length + 8, 4))) throw new InvalidDataException("Invalid PNG CRC.");
            if (pos == 8 && (type != "IHDR" || length != 13)) throw new InvalidDataException("Missing PNG header.");
            yield return (pos, length, type); pos += length + 12;
            if (type == "IEND") { if (length != 0 || pos != png.Length) throw new InvalidDataException("Invalid PNG end."); yield break; }
        }
        throw new InvalidDataException("Missing PNG end.");
    }
    public static bool HasMarker(byte[] png, string marker)
    {
        try { var chunks = Chunks(png).ToArray(); return chunks.Any(c => c.Type == "tEXt" && Encoding.UTF8.GetString(png, c.Offset + 8, c.Length) == "padmaster\0" + marker); }
        catch (InvalidDataException) { return false; }
    }
    static bool HeaderMarker(string path, string marker)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1);
            Span<byte> header = stackalloc byte[41]; file.ReadExactly(header);
            if (!header[..8].SequenceEqual(Signature) || BinaryPrimitives.ReadUInt32BigEndian(header[8..]) != 13 || !header.Slice(12, 4).SequenceEqual("IHDR"u8) || Crc(header.Slice(12, 17)) != BinaryPrimitives.ReadUInt32BigEndian(header[29..]) || !header[37..].SequenceEqual("tEXt"u8)) return false;
            var expected = Encoding.UTF8.GetBytes("padmaster\0" + marker);
            if (BinaryPrimitives.ReadUInt32BigEndian(header[33..]) != expected.Length) return false;
            var chunk = new byte[expected.Length + 8]; header[37..].CopyTo(chunk); file.ReadExactly(chunk.AsSpan(4));
            return chunk.AsSpan(4, expected.Length).SequenceEqual(expected) && Crc(chunk.AsSpan(0, chunk.Length - 4)) == BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(chunk.Length - 4));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
    public static byte[] Mark(byte[] png, string marker)
    {
        var chunks = Chunks(png).ToArray(); var data = Encoding.UTF8.GetBytes("padmaster\0" + marker); var chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, data.Length); Encoding.ASCII.GetBytes("tEXt").CopyTo(chunk, 4); data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8), Crc(chunk.AsSpan(4, data.Length + 4)));
        using var output = new MemoryStream(); output.Write(Signature);
        foreach (var c in chunks)
        {
            if (c.Type == "tEXt" && png.AsSpan(c.Offset + 8, c.Length).StartsWith("padmaster\0"u8)) continue;
            output.Write(png, c.Offset, c.Length + 12); if (c.Type == "IHDR") output.Write(chunk);
        }
        return output.ToArray();
    }
    static int? Overwrite(string path, byte[] png, string marker)
    {
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); using var existing = new MemoryStream(); file.CopyTo(existing);
        var bytes = existing.ToArray(); if (!HasMarker(bytes, marker)) return null;
        if (bytes.AsSpan().SequenceEqual(png)) return 0;
        file.Position = 0; file.Write(png); file.SetLength(png.Length); return 1;
    }
    static Manifest LoadManifest(string path)
    {
        if (!File.Exists(path)) return new(new(), false);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(new(), false);
            return root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Manifest>(root, JsonOptions)!
                : new(JsonSerializer.Deserialize<Dictionary<string, string>>(root)!, false);
        }
        catch (JsonException) { return new(new(), false); }
    }
    static void SaveManifest(string path, Manifest manifest)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, manifest, JsonOptions); file.Flush(true); }
        File.Move(temporary, path, true);
    }
    public static void Refresh(string renderDirectory, Action refresh)
    {
        lock (Gate)
        {
            var path = Path.Combine(renderDirectory, "lb_images.json"); var manifest = LoadManifest(path);
            if (!manifest.RefreshPending) return;
            refresh(); SaveManifest(path, manifest with { RefreshPending = false });
        }
    }
    public static ArtworkResult Write(IGame game, string controller, IReadOnlyList<RenderedState> states, string renderDirectory)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(renderDirectory); var manifest = Path.Combine(renderDirectory, "lb_images.json");
            var stored = LoadManifest(manifest); var paths = stored.Images;
            var written = new Dictionary<string, string>(); var created = 0; var changed = 0; var manifestChanged = false;
            foreach (var state in states)
            {
                var name = state.State.Name; var marker = Marker(game.Title, controller, name); var png = Mark(File.ReadAllBytes(state.FrontPath), marker);
                var hasEntry = paths.TryGetValue(name, out var path); var updated = hasEntry ? Overwrite(path!, png, marker) : null; var added = false;
                if (updated == null)
                {
                    var candidate = game.GetNextAvailableImageFilePath(".png", ImageTypes.ArcadeControlsInformation, null!);
                    if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate)) throw new InvalidDataException("LaunchBox returned no absolute artwork path.");
                    var folder = Path.GetDirectoryName(candidate)!;
                    if (!hasEntry && Directory.Exists(folder)) foreach (var found in Directory.EnumerateFiles(folder, Mapping.FileName(game.Title) + "-*.png").Order(StringComparer.OrdinalIgnoreCase))
                    {
                        if (!HeaderMarker(found, marker)) continue;
                        try { updated = Overwrite(found, png, marker); }
                        catch (IOException) { continue; }
                        catch (UnauthorizedAccessException) { continue; }
                        if (updated != null) { path = found; break; }
                    }
                    if (updated == null)
                    {
                        path = candidate; Directory.CreateDirectory(folder);
                        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(png); created++; added = true;
                    }
                }
                changed += updated ?? 0; written.Add(name, path!);
                var entryChanged = !hasEntry || paths[name] != path; manifestChanged |= entryChanged;
                if (entryChanged || updated > 0 || added) { paths[name] = path!; stored = stored with { RefreshPending = true }; SaveManifest(manifest, stored); }
            }
            return new(written, paths.Where(p => !written.ContainsKey(p.Key)).ToDictionary(), created, changed, manifestChanged, stored.RefreshPending);
        }
    }
}
