using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PadMaster;

static class StuckLegacy
{
    static readonly PropertyInfo? SessionState = typeof(Session).GetProperty("State");
    static readonly MethodInfo? PanelApply = typeof(AssistantPanel).GetMethod("ApplyState");

    public static void Run()
    {
        var log = new ConcurrentQueue<string>();
        var starts = new List<string[]>();
        var counter = Path.Combine(Path.GetTempPath(), "PadMaster-legacy-" + Guid.NewGuid().ToString("N") + ".txt");
        var panel = new AssistantPanel(false) { Log = log.Enqueue };
        panel.SetSetup(new("claude-code", "injected", true));
        panel.SetReady(true);
        var window = new AssistantWindow();
        var placement = new PanelPlacement(panel, window, log.Enqueue, _ => { });
        var host = StateCheck.Host();
        if (!placement.Place("right", host.root, host.details, true, false)) throw new InvalidOperationException("legacy: docking into the synthetic host");
        using var session = new Session("injected", Path.GetTempPath(), "unused", log.Enqueue)
        {
            TurnTimeout = TimeSpan.FromSeconds(3),
            Launch = process => { var arguments = process.StartInfo.ArgumentList.ToArray(); Child(process, counter); lock (starts) starts.Add(arguments); },
        };
        session.Reply += text => panel.Ui(() => panel.SetReply(text));
        session.Activity += text => panel.Ui(() => panel.SetActivity(text));
        session.Failure += text => panel.Ui(() => panel.ShowFailure(text));
        session.Done += success => panel.Ui(() => panel.Finish(success));
        panel.SendRequested = text => session.Send(text);
        Wire(session, panel, "Retry", "RetryRequested");
        Wire(session, panel, "StopTurn", "StopRequested");
        try
        {
            session.AcceptLine("""{"type":"result","subtype":"success","session_id":"resume-me"}""");
            session.Start();
            Until(() => Count(starts) == 1, 15000, "the injected child to start", session, panel, log);
            panel.SubmitText("question the assistant never answers");
            Until(() => Reads(counter) == 1, 15000, "the child to read the typed message", session, panel, log);
            Until(() => Retry(panel) != null, 20000, "a usable Retry after a silent turn; the turn ended with the typed message lost and no way to resend it", session, panel, log);
            if (!Send(panel).IsEnabled) throw new InvalidOperationException("legacy: Send is still disabled after the silent turn");
            Retry(panel)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => Count(starts) == 2, 20000, "Retry to restart the assistant", session, panel, log);
            var second = Snapshot(starts)[1];
            if (!second.Contains("--resume") || !second.Contains("resume-me")) throw new InvalidOperationException("legacy: Retry did not resume the kept session id");
            Until(() => Reads(counter) == 2, 20000, "Retry to resend the message", session, panel, log);
            Pump(300);
            if (Reads(counter) != 2) throw new InvalidOperationException($"legacy: Retry sent the message {Reads(counter)} times");
            Console.WriteLine("OK  stuck legacy: a turn whose child never answers ends with a usable Retry that resumes the session and resends once");
        }
        finally
        {
            session.Shutdown(); placement.Detach(); window.Shutdown();
            try { File.Delete(counter); } catch { }
        }
    }

    static void Wire(Session session, AssistantPanel panel, string method, string callback)
    {
        var call = typeof(Session).GetMethod(method, Type.EmptyTypes);
        var property = typeof(AssistantPanel).GetProperty(callback);
        if (call != null && property != null) property.SetValue(panel, (Action)(() => call.Invoke(session, null)));
    }

    static int Count(List<string[]> starts) { lock (starts) return starts.Count; }
    static string[][] Snapshot(List<string[]> starts) { lock (starts) return [.. starts]; }

    static Button? Retry(AssistantPanel panel) => Buttons(panel).FirstOrDefault(button => Equals(button.Content, "Retry") && button.IsEnabled && button.Visibility == Visibility.Visible);
    static Button Send(AssistantPanel panel) => Buttons(panel).First(button => Equals(button.Content, "Send"));
    static IEnumerable<Button> Buttons(AssistantPanel panel) => PanelPlacement.Walk((DependencyObject)panel.Content).OfType<Button>();
    static int Reads(string counter)
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

    static void Child(Process process, string counter)
    {
        var script = """
            $counter = 'COUNTER'
            while ($true) {
              $line = [Console]::ReadLine()
              if ($line -eq $null) { break }
              [IO.File]::AppendAllText($counter, '1')
            }
            Start-Sleep -Seconds 60
            """.Replace("COUNTER", counter);

        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        process.StartInfo = Session.Command(shell, Path.GetTempPath(), ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))]);
        process.Start();
    }

    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }

    static void Until(Func<bool> ready, int milliseconds, string what, Session session, AssistantPanel panel, ConcurrentQueue<string> log)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!ready())
        {
            if (Environment.TickCount64 > deadline) throw new InvalidOperationException($"legacy: timed out after {milliseconds} ms waiting for {what}; log:\n{string.Join("\n", log)}");
            var state = SessionState?.GetValue(session);
            if (state != null && PanelApply != null) PanelApply.Invoke(panel, [state]);
            Pump(15);
        }
    }
}
