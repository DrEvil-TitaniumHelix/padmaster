using System.IO;
using PadMaster;
using PadMaster.Controls;
using PadMaster.Readers;

static class GenerateCheck
{
    static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("generate: " + message); }
    const string Good = """{"source":{"type":"manual","path":"x"},"states":[{"name":"Default","controls":{"A":"Jump","Left/Right":"Walk"}}],"evidence":{"Default/A":"pdf4: A jumps","Default/Left/Right":"pdf4: Walk"}}""";

    public static async Task Run()
    {
        string[] keys = ["A", "B", "Left", "Right"];
        var withManual = new GenerateRequest("T: x", "NES", @"E:\m.pdf", "NES/m.pdf", keys); var noManual = withManual with { Manual = null, ManualName = "" };
        var (game, problem) = Generator.Parse(withManual, "Here it is\n```json\n" + Good + "\n```");
        Require(problem == null && game!["game"]!.GetValue<string>() == "T: x" && game["source"]!["path"]!.GetValue<string>() == "NES/m.pdf", "good answer saved under LaunchBox's own title and manual path: " + problem);
        Require(Generator.Parse(noManual, Good).Problem!.Contains("manual that doesn't exist"), "manual claimed when there is none");
        Require(Generator.Parse(withManual, Good.Replace("\"A\":\"Jump\"", "\"Triangle\":\"Jump\"")).Problem!.Contains("button not on this system"), "button from another console");
        Require(Generator.Parse(withManual, Good.Replace("Default/A", "Default/B")).Problem!.Contains("no source quote"), "control with no quote");
        Require(Generator.Parse(withManual, Good.Replace("\"Jump\"", "\"one two three four five six seven\"")).Problem!.Contains("1 to 6 words"), "long label");
        Require(Generator.Parse(withManual, Good.Replace("\"Jump\"", "\"Primary action\"")).Problem!.Contains("generic"), "generic label");
        Require(Generator.Parse(withManual, "sorry").Problem!.Contains("wasn't readable"), "prose");
        Require(Generator.Parse(withManual, """{"states":3}""").Problem != null, "wrong shape");
        Require(Generator.Parse(noManual, """{"none":"Nothing on Wikipedia."}""").Problem == "No mapping found for T: x. Nothing on Wikipedia.", "none says where it looked");
        var web = Good.Replace("\"type\":\"manual\",\"path\":\"x\"", "\"type\":\"web\",\"path\":\"https://en.wikipedia.org/wiki/T\"").Replace("pdf4", "https://en.wikipedia.org/wiki/T");
        var (saved, webProblem) = Generator.Parse(noManual, web); Require(webProblem == null && saved!["source"]!["type"]!.GetValue<string>() == "web", "web answer: " + webProblem);
        Require(Mapping.SourceText("web", "https://en.wikipedia.org/wiki/T") == "web en.wikipedia.org" && Mapping.SourceText("manual", "NES/m.pdf") == "manual m.pdf", "picture footer names the source");
        var prompt = Generator.Prompt(noManual, 0); Require(prompt.Contains("\"Left\"") && prompt.Contains("No manual") && !prompt.Contains("{{") && prompt.Contains("GameFAQs") && prompt.Contains(" | ") && prompt.Contains("contradicts"), "prompt");
        var paged = Generator.Prompt(withManual, 12); Require(paged.Contains("page_001.png to page_012.png") && !paged.Contains("No manual") && paged.Contains("contradicts") && !paged.Contains("{{"), "prompt with manual pages");
        Require(Generator.Parse(withManual, Good.Replace("pdf4: A jumps", "pdf4: A jumps | https://gamefaqs.gamespot.com/nes/x: A jumps")).Problem == null, "cross-checked evidence accepted");
        // Codex: same prompt, pages attached as images, JSONL answer read like Claude's.
        var codexArgs = Generator.CodexArguments(@"C:\w", 3); string Joined(IReadOnlyList<string> a) => " " + string.Join(" ", a) + " ";
        Require(codexArgs[0] == "exec" && Joined(codexArgs).Contains(" --json ") && Joined(codexArgs).Contains(" -s read-only ") && Joined(codexArgs).Contains(" -C C:\\w ") && codexArgs.Contains("web_search=live") && codexArgs.Count(a => a == "-i") == 3 && Joined(codexArgs).Contains(" -i page_003.png ") && !codexArgs.Contains("page_004.png"), "codex command line: " + Joined(codexArgs));
        Require(Generator.CodexArguments(@"C:\w", 30).Count(a => a == "-i") == Generator.MaxImages && Generator.MaxImages == 24, "codex attaches at most 24 pages");
        Require(Generator.CodexArguments(@"C:\w", 0).All(a => a != "-i"), "codex with no manual attaches nothing");
        var codexPrompt = Generator.Prompt(withManual, 30, attached: true); Require(codexPrompt.Contains("24 page images are attached (page_001.png to page_024.png, the first 24 of 30 pages)") && codexPrompt.Contains("cannot read any other file") && !codexPrompt.Contains("current folder") && paged.Contains("current folder") && !paged.Contains("attached"), "codex prompt says attached, claude prompt says current folder");
        var logged = new List<string>();
        string[] codexLines = ["""{"type":"thread.started","thread_id":"t"}""", """{"type":"turn.started"}""", "not json", """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Reading page 4."}}""", """{"type":"item.started","item":{"id":"item_1","type":"web_search","query":"x"}}""", """{"type":"item.completed","id":"dup","id":"dup2","item":{"id":"item_1","type":"web_search","query":"x"}}""",new System.Text.Json.Nodes.JsonObject { ["type"] = "item.completed", ["item"] = new System.Text.Json.Nodes.JsonObject { ["id"] = "item_2", ["type"] = "agent_message", ["text"] = "```json\n" + Good + "\n```" } }.ToJsonString(), """{"type":"turn.completed","usage":{"input_tokens":5000,"output_tokens":75}}"""];
        var (fromCodex, codexProblem) = Generator.CodexAnswer(withManual, codexLines, logged.Add); var (fromClaude, _) = Generator.Parse(withManual, Good);
        Require(codexProblem == null && System.Text.Json.Nodes.JsonNode.DeepEquals(fromCodex, fromClaude) && logged.Any(l => l.Contains("tokens in 5000 out 75")), "codex answer read like claude's: " + codexProblem);
        Require(Generator.CodexAnswer(withManual, [codexLines[1], """{"type":"turn.failed","error":{"message":"usage limit reached"}}"""], logged.Add).Problem == "Codex couldn't finish: usage limit reached", "codex failure event");
        Require(Generator.CodexAnswer(withManual, [codexLines[1], """{"type":"error","message":"stream lost"}"""], logged.Add).Problem == "Codex couldn't finish: stream lost", "codex error event");
        Require(Generator.CodexAnswer(withManual, [codexLines[1]], logged.Add).Problem == "Codex couldn't finish: see padmaster.log", "codex with no answer");
        Require(Generator.CodexAnswer(withManual, [codexLines[3]], logged.Add).Problem!.StartsWith("Codex's answer wasn't readable"), "codex prose names Codex");
        var pdf = Path.Combine(LaunchBoxDir.Value, "Manuals", "Nintendo Entertainment System", "Contra.pdf"); var scratch = Directory.CreateTempSubdirectory("PadMaster-pdf").FullName;
        var pages = await Generator.RenderPdf(pdf, scratch, CancellationToken.None); using (var first = SkiaSharp.SKBitmap.Decode(Path.Combine(scratch, "page_001.png"))) Require(pages > 3 && Directory.GetFiles(scratch, "*.png").Length == pages && Math.Max(first.Width, first.Height) >= 1998, $"PDF pages become pictures: {pages} pages, first {first.Width}x{first.Height}");
        Directory.Delete(scratch, true);
        Console.WriteLine($"OK  generate: answers checked before saving; manual and web sources; Codex command line, prompt and JSONL answer; Contra.pdf -> {pages} page pictures");
    }

    // A real run on the user's own subscription, saved to a scratch folder. LaunchBox's files are not touched.
    public static async Task Live(string platform, string title, string assistant = "claude-code")
    {
        var root = LaunchBoxDir.Value; var read = Select.Read(root, platform, title); Require(read.Error == null && read.Bindings.Count > 0, "reader: " + read.Error);
        var manual = Path.Combine(root, "Manuals", Mapping.FileName(platform), Mapping.FileName(title) + ".pdf");
        var request = GenerateRequest.For(title, platform, File.Exists(manual) ? manual : null, read.Bindings.Keys, root); Console.WriteLine("keys: " + string.Join(", ", read.Bindings.Keys) + "\nmanual: " + (request.Manual ?? "none"));
        var (game, problem) = await Generator.Run(request, Console.WriteLine, Console.WriteLine, CancellationToken.None, assistant);
        if (game == null) { Console.WriteLine("RESULT: " + problem); return; }
        var home = Path.Combine(Path.GetTempPath(), "PadMaster-generate-live"); Mapping.WriteGame(home, game); var json = Mapping.GamePath(home, platform, title); Console.WriteLine(json);
        var repo = new DirectoryInfo(AppContext.BaseDirectory); while (!Directory.Exists(Path.Combine(repo.FullName, "controllers"))) repo = repo.Parent!;
        foreach (var s in Mapping.Render(Mapping.Controller(repo.FullName), json, read.Bindings, Path.Combine(Mapping.RenderDirectory(home, platform, title), "live"), read.Config, null, "")) Console.WriteLine($"{s.State.Name}: {s.FrontPath}" + (s.State.Unbound.Count > 0 ? "  not bound: " + string.Join(", ", s.State.Unbound) : ""));
    }
}
