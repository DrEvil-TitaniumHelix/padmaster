using System.Text.RegularExpressions;

namespace PadMaster.Readers;

public static class ShadPs4
{
    const string Default = "triangle = triangle\ncross = cross\nsquare = square\ncircle = circle\nl1 = l1\nl2 = l2\nl3 = l3\nr1 = r1\nr2 = r2\nr3 = r3\noptions = options\ntouchpad_center = back\npad_up = pad_up\npad_down = pad_down\npad_left = pad_left\npad_right = pad_right\naxis_left_x = axis_left_x\naxis_left_y = axis_left_y\naxis_right_x = axis_right_x\naxis_right_y = axis_right_y"; // v.0.21.0 src/input/input_handler.cpp GetDefaultInputConfig, controller section
    static readonly Dictionary<string, string> Keys = new() { ["triangle"] = "Y", ["cross"] = "A", ["square"] = "X", ["circle"] = "B", ["l1"] = "lb", ["l2"] = "lt", ["l3"] = "ls", ["r1"] = "rb", ["r2"] = "rt", ["r3"] = "rs", ["options"] = "start", ["back"] = "back", ["pad_up"] = "dpad_up", ["pad_down"] = "dpad_down", ["pad_left"] = "dpad_left", ["pad_right"] = "dpad_right", ["axis_left_x"] = "ls", ["axis_left_y"] = "ls", ["axis_right_x"] = "rs", ["axis_right_y"] = "rs" };
    static readonly Dictionary<string, string> Names = new() { ["triangle"] = "Triangle", ["cross"] = "Cross", ["square"] = "Square", ["circle"] = "Circle", ["l1"] = "L1", ["l2"] = "L2", ["l3"] = "L3", ["r1"] = "R1", ["r2"] = "R2", ["r3"] = "R3", ["options"] = "Options", ["touchpad_center"] = "Touchpad", ["pad_up"] = "Up", ["pad_down"] = "Down", ["pad_left"] = "Left", ["pad_right"] = "Right", ["axis_left_x"] = "Left stick", ["axis_left_y"] = "Left stick", ["axis_right_x"] = "Right stick", ["axis_right_y"] = "Right stick" };

    internal static Dictionary<string, string> Parse(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim(); var i = line.IndexOf('=');
            if (i < 0 || !Names.TryGetValue(line[..i].Trim(), out var name)) continue;
            foreach (var input in line[(i + 1)..].Split(',', StringSplitOptions.TrimEntries)) if (Keys.TryGetValue(input, out var key)) result[name] = key;
        }
        return result;
    }

    public static ReadResult Read(string exe, string? rom)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
        var user = Directory.Exists(Path.Combine(dir, "user")) ? Path.Combine(dir, "user") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "shadPS4");
        var folder = Path.Combine(user, "input_config");
        var id = rom == null ? null : Regex.Match(rom, @"CUSA\d{5}") is { Success: true } m ? m.Value : null;
        var file = new[] { id == null ? null : Path.Combine(folder, id + ".ini"), Path.Combine(folder, "default.ini") }.FirstOrDefault(File.Exists);
        if (file != null) { var read = Parse(File.ReadAllText(file)); return new(read, [], read.Count == 0 ? "input config has no controller bindings" : null, file); }
        return new(Parse(Default), ["shadPS4 built-in default (v.0.21.0 src/input/input_handler.cpp GetDefaultInputConfig); no input_config file on this PC yet"], null, folder);
    }
}
