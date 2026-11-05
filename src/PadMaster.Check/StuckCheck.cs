using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PadMaster;

static class StuckCheck
{
    static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "stuck_fixture.jsonl");
    static readonly string Reply = "REPLY-OK: fixture reply.";
    static readonly string[] Forbidden = ["chat update", "dropped", "session mismatch", "assistant process:", "ignored assistant output", "stalled"];

    public static void Run()
    {
        Require(File.Exists(Fixture), "fixture is staged beside the check");
        var lines = File.ReadAllLines(Fixture).Length;
        Require(lines == 185, $"fixture has 185 lines, found {lines}");
        Baseline();
        Restart();
        Stale();
        Replaced();
        Exit();
        Silence();
        Queue();
        Held();
        Mic();
        HostSweep();
        Console.WriteLine("OK  stuck: baseline, restart, stale session, replaced session, child exit, 90 s silence with Retry and resume, queued second message, held guard, microphone across re-parenting, host Dispose() sweep");
    }

    sealed class Lane : IDisposable
    {
        public readonly ConcurrentQueue<string> Log = new();
        readonly List<string[]> _starts = [];
        public string[][] Starts { get { lock (_starts) return [.. _starts]; } }
        public readonly AssistantPanel Panel;
        public readonly AssistantWindow Window = new();
        public readonly PanelPlacement Placement;
        public readonly string Counter = Path.Combine(Path.GetTempPath(), "PadMaster-stuck-" + Guid.NewGuid().ToString("N") + ".txt");
        public Session Session = null!;
        public int Pid { get { lock (_starts) return _pid; } }
        int _pid;
        public ChatBridge Bridge = null!;
        public Session? Current;
        long _clock = new DateTime(2026, 11, 3, 14, 4, 38, DateTimeKind.Utc).Ticks;
        public DateTime Clock => new(Interlocked.Read(ref _clock), DateTimeKind.Utc);
        public void Advance(TimeSpan amount) => Interlocked.Add(ref _clock, amount.Ticks);
        public int Pace = 1;
        public bool Silent;
        readonly (Grid root, UserControl details) _host = StateCheck.Host();

        public Lane(bool dock = true)
        {
            Panel = new AssistantPanel(false) { Log = Log.Enqueue };
            Placement = new PanelPlacement(Panel, Window, Log.Enqueue, _ => { });
            if (dock) Require(Placement.Place("right", _host.root, _host.details, true, false), "docked into the synthetic host");
            Panel.SetSetup(new("claude-code", "injected", true));
            Panel.SetReady(true);
        }

        public Session Open(Action<Session>? configure = null)
        {
            var session = new Session("injected", Path.GetTempPath(), "unused", Log.Enqueue)
            {
                Now = () => Clock,
                WatchdogInterval = TimeSpan.FromMilliseconds(50),
                Launch = process => { var arguments = process.StartInfo.ArgumentList.ToArray(); Child(process, Fixture, Counter, Pace, Silent); lock (_starts) { _pid = process.Id; _starts.Add(arguments); } },
            };
            configure?.Invoke(session);
            Session = session; Current = session;
            Panel.SendRequested = text => Current?.Send(text);
            Bridge = new ChatBridge(session, Panel, () => Current, () => false, Log.Enqueue);
            session.Start();
            return session;
        }

        public int Reads() => Counted(Counter);
        public void Place(string placement) => Placement.Place(placement, _host.root, _host.details, true, false);
        public int Sweep()
        {
            var disposed = 0;
            foreach (var visual in PanelPlacement.Walk(_host.root)) if (visual is IDisposable disposable) { disposable.Dispose(); disposed++; }
            return disposed;
        }
        public string Text() => string.Join("\n", Log);
        public void Dispose() { try { Session?.Shutdown(); } catch { } Placement.Detach(); Window.Shutdown(); Panel.Shutdown(); try { File.Delete(Counter); } catch { } }
    }

    static int Counted(string counter)
    {
        for (var attempt = 0; ; attempt++)
            try
            {
                using var stream = new FileStream(counter, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return (int)stream.Length;
            }
            catch (FileNotFoundException) { return 0; }
            catch (IOException) when (attempt < 50) { Thread.Sleep(10); }
    }

    static void Child(Process process, string fixture, string counter, int pace, bool silent)
    {
        var script = """
            $fixture = 'FIXTURE'; $counter = 'COUNTER'; $pace = PACE; $silent = $SILENT
            while ($true) {
              $line = [Console]::ReadLine()
              if ($line -eq $null) { break }
              [IO.File]::AppendAllText($counter, '1')
              if ($silent) { continue }
              foreach ($event in [IO.File]::ReadLines($fixture)) { [Console]::WriteLine($event); if ($pace -gt 0) { Start-Sleep -Milliseconds $pace } }
            }
            Start-Sleep -Seconds 60
            """.Replace("FIXTURE", fixture).Replace("COUNTER", counter).Replace("PACE", pace.ToString()).Replace("SILENT", silent.ToString());

        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        process.StartInfo = Session.Command(shell, Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        process.Start();
    }

    static void Baseline()
    {
        using var lane = new Lane();
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the injected child to start", lane);
        lane.Panel.SubmitText("question about the library");
        Until(() => lane.Panel.Transcript.Contains(Reply), 20000, "the reply to reach the panel", lane);
        Until(() => !lane.Panel.Busy, 10000, "the turn to finish", lane);
        Require(lane.Panel.SendEnabled && !lane.Panel.CanStop && !lane.Panel.CanRetry, "finished turn leaves Send usable with no Stop or Retry");
        Require(lane.Session.Lines == 185 && lane.Session.ReplyRevision > 0, $"all 185 stream lines parsed, got {lane.Session.Lines}");
        var text = lane.Text();
        Require(text.Contains("turn lines:") && text.Contains("total=185") && text.Contains("result=1"), "turn line counts logged by type");
        Require(text.Contains("result seen:") && text.Contains("subtype=success") && text.Contains("is_error=False"), "result subtype and error flag logged");
        Require(text.Contains("first panel update:") && text.Contains("panel created:"), "panel creation and first update thread facts logged");
        Require(!text.Contains(Reply) && !text.Contains("question about the library"), "no reply or typed text in the log");
        foreach (var word in Forbidden) Require(!text.Contains(word), $"a healthy turn never logs \"{word}\"");
        Console.WriteLine($"OK  stuck baseline: 185 fixture lines through the real session, panel shows the reply, {lane.Session.ReplyRevision} reply updates");
    }

    static void Restart()
    {
        using (var first = new Lane())
        {
            first.Open();
            Until(() => first.Starts.Length == 1, 10000, "the first child", first);
            first.Panel.SubmitText("first question");
            Until(() => first.Panel.Transcript.Contains(Reply), 20000, "the first reply", first);
        }
        using var lane = new Lane();
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the child after the restart", lane);
        lane.Panel.SubmitText("question after the restart");
        Until(() => lane.Panel.Transcript.Contains(Reply), 20000, "the reply after a restart", lane);
        Until(() => !lane.Panel.Busy && lane.Panel.SendEnabled, 10000, "the panel to clear after a restart", lane);
        foreach (var word in Forbidden) Require(!lane.Text().Contains(word), $"a fresh lifecycle never logs \"{word}\"");
        Console.WriteLine("OK  stuck restart: fresh panel, session and auto-open deliver the reply after a shutdown");
    }

    static void Stale()
    {
        using var lane = new Lane();
        var stale = lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the stale child", lane);
        lane.Current = new Session("injected", Path.GetTempPath(), "unused", _ => { });
        stale.AcceptLine("""{"type":"assistant","message":{"id":"stale","content":[{"type":"text","text":"STALE-REPLY"}]}}""");
        Until(() => lane.Text().Contains("panel update dropped: session mismatch"), 10000, "the dropped update to be logged", lane);
        Require(!lane.Panel.Transcript.Contains("STALE-REPLY"), "a replaced session never writes into the panel");
        lane.Current.Dispose();
        Console.WriteLine("OK  stuck stale session: the replaced session is refused and the drop is logged with its reason");
    }

    static void Replaced()
    {
        using var lane = new Lane();
        var first = lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the child of the first session", lane);
        lane.Panel.SubmitText("question before the session is replaced");
        Until(() => Replies(lane) == 1, 20000, "the reply of the first session", lane);
        Until(() => !lane.Panel.Busy, 10000, "the first turn to finish", lane);
        var stamps = lane.Session.State.Stamp;
        Require(stamps > 20, $"a long-lived session accumulates state stamps, got {stamps}");
        first.Shutdown();
        lane.Open();
        Until(() => lane.Starts.Length == 2, 10000, "the child of the replacement session", lane);
        lane.Panel.SubmitText("question after the session is replaced");
        Pump(400);
        Require(lane.Panel.CanStop && lane.Panel.Busy, "the replacement session's running turn reaches the panel at once");
        Require(lane.Session.State.Stamp > stamps, "a replacement session continues the stamp sequence");
        first.AcceptLine("""{"type":"assistant","message":{"id":"stale","content":[{"type":"text","text":"STALE-AFTER-REPLACEMENT"}]}}""");
        Until(() => lane.Text().Contains("panel update dropped: session mismatch"), 10000, "the late update from the replaced session to be refused", lane);
        Require(!lane.Panel.Transcript.Contains("STALE-AFTER-REPLACEMENT"), "a late reply from the replaced session never reaches the panel");
        Until(() => Replies(lane) == 2, 20000, "the reply of the replacement session", lane);
        Until(() => !lane.Panel.Busy && !lane.Panel.CanStop, 10000, "the replacement turn to finish", lane);
        Require(lane.Panel.SendEnabled, "Send is usable after the session was replaced");
        first.Dispose();
        Console.WriteLine($"OK  stuck replaced session: a new session after {stamps} stamps applies its turn state at once and the old one is still refused");
    }

    static int Replies(Lane lane)
    {
        var transcript = lane.Panel.Transcript; var count = 0;
        for (var at = transcript.IndexOf(Reply, StringComparison.Ordinal); at >= 0; at = transcript.IndexOf(Reply, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }

    static void Exit()
    {
        using var lane = new Lane { Silent = true };
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the child before the kill", lane);
        lane.Panel.SubmitText("question that never answers");
        Until(() => lane.Reads() == 1, 10000, "the child to read the message", lane);
        lane.Panel.SubmitText("second message while busy");
        Require(lane.Panel.Notes.Contains("queued"), "a second message during a turn is marked queued");
        using (var child = Process.GetProcessById(lane.Pid)) { child.Kill(true); child.WaitForExit(5000); }
        Until(() => !lane.Panel.Busy, 15000, "the panel to clear after the child exits", lane);
        Require(lane.Panel.SendEnabled && !lane.Panel.CanStop, "Send is usable again after the child exits");
        Require(Find<TextBlock>(lane.Panel, "PadActivity").Text.Contains("restarted"), "the panel says the assistant restarted and the message should be sent again");
        Pump(500);
        Require(lane.Reads() == 1, "a queued message is not sent automatically after a child exit");
        Require(lane.Text().Contains("assistant exited code="), "the child exit is logged with its code");
        Console.WriteLine("OK  stuck child exit: an exiting child clears busy, re-enables Send, keeps the queued message unsent");
    }

    static void Silence()
    {
        using var lane = new Lane { Silent = true };
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the silent child", lane);
        lane.Session.AcceptLine("""{"type":"result","subtype":"success","session_id":"resume-me"}""");
        lane.Panel.SubmitText("question the assistant never answers");
        Until(() => lane.Reads() == 1, 10000, "the silent child to read the message", lane);
        Require(lane.Panel.Busy && lane.Panel.CanStop && !lane.Panel.CanRetry, "a running turn shows Stop and no Retry");
        lane.Session.ToolActivity(true, true); lane.Session.ToolActivity(false, true);
        lane.Advance(TimeSpan.FromSeconds(45));
        Pump(400);
        Require(lane.Panel.Busy && !lane.Panel.CanRetry, "45 seconds of silence does not interrupt the turn");
        lane.Advance(TimeSpan.FromSeconds(50));
        Until(() => lane.Panel.CanRetry, 10000, "the 90 second watchdog to offer Retry", lane);
        Require(!lane.Panel.Busy && lane.Panel.SendEnabled && !lane.Panel.CanStop, "the watchdog clears busy and re-enables Send");
        var text = lane.Text();
        Require(text.Contains("turn watchdog:") && text.Contains("lines=0"), "the watchdog logs its reason with the line count");
        Require(Find<TextBlock>(lane.Panel, "PadActivity").Text.Contains("no answer"), "the panel says the assistant did not answer");
        Require(FindAll<TextBlock>(lane.Panel).Any(line => line.Text.Contains("may repeat a change")), "a write turn warns that Retry may repeat a change");
        Find<Button>(lane.Panel, "PadRetry").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Until(() => lane.Starts.Length == 2, 15000, "Retry to restart the assistant", lane);
        Require(lane.Starts[1].Contains("--resume") && lane.Starts[1].Contains("resume-me"), "Retry resumes the kept session id");
        Until(() => lane.Reads() == 2, 15000, "Retry to resend the message once", lane);
        Pump(300);
        Require(lane.Reads() == 2, "Retry sends the message exactly once");
        Console.WriteLine("OK  stuck silence: a 90 s silent turn offers Retry with the write warning, resumes the kept session id and resends exactly once");
    }

    static void Queue()
    {
        using var lane = new Lane { Pace = 12 };
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the paced child", lane);
        lane.Panel.SubmitText("first paced question");
        Until(() => lane.Reads() == 1, 10000, "the first message to reach the child", lane);
        Require(lane.Panel.Busy && lane.Panel.SendEnabled, "Send stays enabled during a turn so a message can be queued");
        lane.Panel.SubmitText("second paced question");
        Require(lane.Panel.Notes.Contains("queued") && lane.Panel.Transcript.Contains("second paced question"), "the queued message is shown with a queued mark");
        Require(lane.Reads() == 1, "the queued message waits for the running turn");
        Until(() => lane.Reads() == 2, 25000, "the queued message to be sent after the result", lane);
        Until(() => !lane.Panel.Busy, 20000, "both turns to finish", lane);
        Require(lane.Panel.SendEnabled && lane.Session.Starts == 1, "both turns ran through one child");
        Console.WriteLine("OK  stuck queue: a second message queues during a turn and is sent once after the result");
    }

    static void Held()
    {
        using var lane = new Lane { Silent = true };
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the child before the hold", lane);
        lane.Panel.SubmitText("question that waits for a confirmation");
        Until(() => lane.Reads() == 1, 10000, "the held child to read the message", lane);
        lane.Session.SetHeld(true);
        lane.Advance(TimeSpan.FromMinutes(10));
        Pump(500);
        Require(!lane.Panel.CanRetry && !lane.Session.State.Busy, "a pending guard bar is not a turn and never trips the watchdog");
        Require(!lane.Text().Contains("turn watchdog:"), "the watchdog does not fire while a confirmation is held");
        lane.Session.SetHeld(false);
        lane.Advance(TimeSpan.FromSeconds(91));
        Until(() => lane.Panel.CanRetry, 10000, "the watchdog to resume after the hold", lane);
        Console.WriteLine("OK  stuck held guard: ten held minutes do not trip the watchdog, which resumes once the guard clears");
    }

    static void Mic()
    {
        using var lane = new Lane();
        var toggles = new List<bool>();
        lane.Panel.SetMicrophone(Microphone.Injected(toggles.Add));
        var button = Find<Button>(lane.Panel, "PadMic");
        Require(button.IsEnabled, "an available recognizer enables the microphone");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(toggles.SequenceEqual(new[] { true }), "the first click starts dictation");
        foreach (var placement in new[] { "left", "floating", "right" })
        {
            lane.Place(placement); Pump(60);
            Require(button.IsEnabled, $"the microphone survives the move to {placement}");
        }
        lane.Panel.OpenConfiguration(); Pump(40);
        lane.Panel.ConfigurationAccepted(new Settings { Assistant = "claude-code" }); Pump(40);
        Require(button.IsEnabled, "the microphone survives the setup card and Back to chat");
        lane.Panel.Suspend(); Pump(40);
        Require(button.IsEnabled, "the microphone survives Suspend");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(toggles.Count >= 2 && toggles[^1], "the microphone still reaches the recognizer after every move");
        Require(!lane.Text().Contains("mic unavailable"), "re-parenting never reports the microphone as unavailable");
        Console.WriteLine($"OK  stuck microphone: enabled through right, left, floating, setup, Back and Suspend; {toggles.Count} recognizer calls, no real audio device");
    }

    static void HostSweep()
    {
        using var lane = new Lane();
        lane.Open();
        Until(() => lane.Starts.Length == 1, 10000, "the child before the host sweep", lane);
        var toggles = 0; lane.Panel.SetMicrophone(Microphone.Injected(_ => toggles++));
        var swept = 0;
        foreach (var placement in new[] { "right", "left", "right" }) { lane.Place(placement); swept += lane.Sweep(); Pump(40); }
        lane.Panel.SubmitText("question after the host swept its details view");
        Pump(300);
        Require(!lane.Text().Contains("panel update dropped"), "a turn after the host's details-view sweep logs no dropped panel update; log: " + string.Join(" | ", lane.Log.Where(line => line.Contains("dropped"))));
        Until(() => lane.Panel.Transcript.Contains(Reply), 20000, "the reply to reach the panel through events alone", lane, false);
        Until(() => !lane.Panel.Busy, 10000, "the turn to finish through events alone", lane, false);
        Find<Button>(lane.Panel, "PadMic").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(toggles == 1, "the microphone survives the host sweep");
        Require(swept == 0, $"LaunchBox's details-view sweep finds no IDisposable in the panel's tree, found {swept}");
        Require(lane.Log.Count(line => line.Contains("panel created:")) == 1, "one panel instance per process");
        Console.WriteLine($"OK  stuck host sweep: LaunchBox's IDisposable walk over its details view disposes nothing of ours across right, left, right; reply through events alone, {lane.Session.ReplyRevision} reply updates, zero drops");
    }

    static T Find<T>(AssistantPanel panel, string id) where T : FrameworkElement =>
        FindAll<T>(panel).FirstOrDefault(element => AutomationProperties.GetAutomationId(element) == id)
        ?? throw new InvalidOperationException($"stuck: no {typeof(T).Name} with AutomationId {id}");

    static IEnumerable<T> FindAll<T>(AssistantPanel panel) where T : FrameworkElement =>
        PanelPlacement.Walk((DependencyObject)panel.Content).OfType<T>();

    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }

    static void Until(Func<bool> ready, int milliseconds, string what, Lane lane, bool heal = true)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!ready())
        {
            if (Environment.TickCount64 > deadline) throw new InvalidOperationException($"stuck: timed out after {milliseconds} ms waiting for {what}; log:\n{lane.Text()}");
            if (heal) lane.Bridge?.Heal();
            Pump(15);
        }
    }

    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException("stuck: " + message); }
}
