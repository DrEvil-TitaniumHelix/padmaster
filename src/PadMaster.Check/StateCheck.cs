using System.Windows;
using System.Windows.Controls;
using PadMaster;

static class StateCheck
{
    static void Require(bool value, string message) => ReviewCheck.Require(value, message);

    public static (Grid root, UserControl details) Host()
    {
        var details = new UserControl { Content = new Grid { Children = { new ScrollViewer { Content = new Grid() } } } };
        var side = new ContentControl { Name = "SideBarViewModel", Content = new Grid() };
        return (new Grid { Children = { side, new ContentControl { Content = details } } }, details);
    }

    public static void Run()
    {
        var panel = new AssistantPanel(false);
        var window = new AssistantWindow();
        var log = new List<string>(); var shown = 0; var configures = 0; var stops = 0;
        var placer = new PanelPlacement(panel, window, log.Add, _ => shown++);
        var (root, details) = Host();
        var host = new PlacementHost(true, root, details, true);
        var current = new Settings { Enabled = true, Assistant = "claude-code", Cli = @"C:\claude.exe", Placement = "floating", Revision = 1 };
        var driver = new PlacementDriver(placer, () => host);
        var lifecycle = new AssistantLifecycle(_ => log.Add("mcp up"), () => { }, settings => panel.LoadConfiguration(settings), panel.OpenConfiguration, () => { },
            startup => driver.Open(current.Placement, startup), () => configures++, () => { stops++; driver.Stop(); placer.Detach(); window.Hide(); });
        var gate = new ActivationGate(lifecycle.Start, (previous, next) => { current = next; lifecycle.Saved(previous, next, driver.Shown, log.Add); }, lifecycle.Stop, log.Add);
        static ActivationState State(Settings settings) => new(settings, false, true);
        int Docked() => PanelPlacement.Walk(root).Count(x => ReferenceEquals(x, panel));
        gate.Update(State(current with { Enabled = false }));
        Require(!driver.Opened && shown == 0, "disabled start stays hidden");
        gate.Update(State(current));
        Require(driver.Opened && placer.Floating && shown == 1 && configures == 1 && log.Contains("mcp up"), "enable on opens at floating");
        driver.CloseFloating();
        Require(!driver.Opened, "closing the floating window hides");
        var lines = log.Count; var places = driver.PlaceCalls;
        gate.Update(State(current with { Revision = 2 }));
        Require(driver.Opened && placer.Floating && shown == 2 && driver.PlaceCalls == places + 1 && log.Count == lines + 1, "save with Enable from hidden re-shows at floating and logs one line");
        Require(stops == 0 && configures == 2, "session is not stopped across close and re-show");
        lines = log.Count; places = driver.PlaceCalls;
        gate.Update(State(current with { Revision = 3 }));
        Require(driver.Opened && driver.PlaceCalls == places && shown == 2 && log.Count == lines, "repeat save while shown places nothing new");
        gate.Update(State(current with { Revision = 4, Placement = "right" }));
        Require(driver.Opened && !placer.Floating && Docked() == 1, "placement change moves into the right host once");
        gate.Update(State(current with { Revision = 5, Placement = "left" }));
        Require(driver.Opened && !placer.Floating && Docked() == 1, "placement change moves into the left host once");
        gate.Update(State(current with { Enabled = false }));
        Require(stops == 1 && !driver.Opened && Docked() == 0 && PanelPlacement.Parent(panel) == null, "enable off hides at once and stops");
        gate.Update(State(current with { Enabled = true, Revision = 6 }));
        Require(driver.Opened && !placer.Floating && Docked() == 1 && configures == 3, "enable on again opens at left");
        gate.Update(State(current with { Revision = 7, Placement = "floating" }));
        Require(driver.Opened && placer.Floating && Docked() == 0 && shown == 3, "placement change back to floating");
        driver.CloseFloating(); Require(!driver.Opened, "closing hides again");
        gate.Open(() => lifecycle.Open(current, false));
        Require(driver.Opened && shown == 4 && stops == 1, "Tools re-shows the closed window");
        host = host with { Details = null, DetailsVisible = false };
        gate.Update(State(current with { Revision = 8, Placement = "right" }));
        Require(driver.Opened && placer.Floating && shown == 5, "right without game details opens temporary floating fallback");
        driver.CloseFloating(); window.Hide();
        Require(driver.Opened && !driver.Shown && placer.Floating, "closing fallback keeps automatic redocking active");
        lines = log.Count; places = driver.PlaceCalls;
        gate.Update(State(current));
        Require(shown == 5 && driver.PlaceCalls == places && log.Count == lines, "unchanged revision keeps closed fallback suppressed");
        gate.Update(State(current with { Revision = 9 }));
        Require(driver.Opened && driver.Shown && placer.Floating && shown == 6 && driver.PlaceCalls == places + 1 && log.Count == lines + 1 && log[^1] == "settings saved; showing the assistant (right)", "new Save with Enable re-shows suppressed right fallback and logs one line");
        lines = log.Count; places = driver.PlaceCalls;
        gate.Update(State(current with { Revision = 10 }));
        Require(shown == 6 && driver.PlaceCalls == places && log.Count == lines, "repeat Save while fallback shown stays silent with no extra Place");
        host = host with { Details = details, DetailsVisible = true }; driver.Refresh();
        Require(driver.Opened && !placer.Floating && Docked() == 1, "re-shown fallback still redocks when game details return");
        placer.Detach(); window.Shutdown();
        Console.WriteLine("OK  state machine: hidden/shown x enabled/disabled x placement, Save re-shows suppressed fallback once, unchanged/repeat Save stays silent, redocks through the real gate, lifecycle and driver");
    }
}
