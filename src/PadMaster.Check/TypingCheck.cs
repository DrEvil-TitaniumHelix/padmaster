using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PadMaster;

static class TypingCheck
{
    [DllImport("user32.dll")] static extern bool PostMessage(nint hwnd, uint message, nint w, nint l);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint map);
    [DllImport("user32.dll")] static extern nint GetFocus();
    const uint KeyDownMessage = 0x100, KeyUpMessage = 0x101;
    const int VkReturn = 0x0D, VkA = 0x41, VkSpace = 0x20;

    public static void Run()
    {
        var panel = new AssistantPanel(false);
        panel.SetSetup(new("claude-code", @"C:\claude.exe", true)); panel.SetReady(true);
        var sent = new List<string>(); panel.SendRequested = sent.Add;
        var window = new AssistantWindow();
        var (root, details) = StateCheck.Host();
        var placer = new PanelPlacement(panel, window, _ => { });
        if (!placer.Place("right", root, details, true, false)) throw new InvalidOperationException("dock into synthetic host");
        var host = new ContentControl { Focusable = false, IsTabStop = false, Content = root };
        var parameters = new HwndSourceParameters("PadMaster typing check", 600, 600) { WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x80 | 0x08000000, PositionX = -32000, PositionY = -32000 };
        using var source = new HwndSource(parameters) { RootVisual = host };
        try
        {
            Pump(50);
            if (!panel.FocusInput() || Keyboard.FocusedElement is not TextBox) throw new InvalidOperationException($"input focus: focused={Keyboard.FocusedElement?.GetType().Name ?? "null"} hwndFocus={GetFocus() == source.Handle} panelVisible={panel.IsVisible}");
            foreach (var key in new[] { VkA, VkSpace, VkA }) { Key(source.Handle, key); Pump(100); }
            if (!string.Equals(panel.Draft, "a a", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"typed text: expected 'a a' got '{panel.Draft}' (len {panel.Draft.Length}); KeyDown reached the box but no WM_CHAR followed");
            Key(source.Handle, VkReturn); Pump(250);
            if (sent.Count != 1 || !string.Equals(sent[0], "a a", StringComparison.OrdinalIgnoreCase) || panel.Draft.Length != 0) throw new InvalidOperationException($"enter sends: sent={sent.Count} draft='{panel.Draft}'");
            Console.WriteLine("OK  typing: WM_KEYDOWN -> TranslateMessage -> WM_CHAR -> TextInput reaches the docked TextBox; Enter sends and clears");
        }
        finally { placer.Detach(); source.RootVisual = null; window.Shutdown(); panel.Shutdown(); }
    }

    static void Key(nint hwnd, int key)
    {
        var scan = (long)MapVirtualKey((uint)key, 0);
        PostMessage(hwnd, KeyDownMessage, key, (nint)(1 | scan << 16));
        PostMessage(hwnd, KeyUpMessage, key, (nint)(1 | scan << 16 | 0xC0000000));
    }

    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
    }
}
