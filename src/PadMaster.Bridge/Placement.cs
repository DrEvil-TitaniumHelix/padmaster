using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace PadMaster;

public sealed class PanelPlacement(AssistantPanel panel, AssistantWindow window, Action<string> log, Action<AssistantWindow>? show = null)
{
    readonly Action<AssistantWindow>? thisShow = show;
    FrameworkElement? _details, _main, _rightAnchor, _leftAnchor;
    Grid? _wrapper;
    FrameworkElement? _anchor;
    Panel? _parent;
    int _index;
    string? _lastFailure;
    public bool Floating { get; private set; }
    public string Status { get; private set; } = "";
    public bool IsRightHostVisible(FrameworkElement? details) => !ReferenceEquals(_details, details) || _rightAnchor?.IsVisible != false;

    public bool Place(string placement, FrameworkElement? main, FrameworkElement? details, bool detailsVisible = true, bool showWindow = true)
    {
        try
        {
            Detach();
            if (placement == "floating") { Float(showWindow, ""); return true; }
            if (placement == "right")
            {
                if (!ReferenceEquals(_details, details))
                {
                    _details = details;
                    _rightAnchor = details == null ? null : Walk(details).OfType<ScrollViewer>().FirstOrDefault();
                    if (details != null) panel.UseTheme(details);
                }
                if (details == null || !detailsVisible) return Fallback("Right panel is unavailable; using floating window.", showWindow);
                if (_rightAnchor == null) return Fallback("This theme has no right chat host; using floating window.", showWindow);
                Insert(_rightAnchor, false);
            }
            else
            {
                if (!ReferenceEquals(_main, main))
                {
                    _main = main;
                    _leftAnchor = main == null ? null : Walk(main).OfType<ContentControl>().FirstOrDefault(x => x.Name == "SideBarViewModel");
                    if (_leftAnchor != null) panel.UseTheme(_leftAnchor);
                }
                if (_leftAnchor == null) return Fallback("This theme has no sidebar chat host; using floating window.", showWindow);
                if (_leftAnchor.Visibility != Visibility.Visible) return Fallback("The sidebar is hidden; using floating window.", showWindow);
                Insert(_leftAnchor, true);
            }
            Floating = false; Status = ""; _lastFailure = null; window.Hide(); return true;
        }
        catch (Exception e)
        {
            return Fallback("Chat could not dock; using floating window.", showWindow, SafeDiagnostics.Error(e));
        }
    }

    public static bool NeedsPlacement(FrameworkElement? previous, object? current, bool previousVisible, bool visible, bool previousUnavailable, bool unavailable) => !ReferenceEquals(previous, current as FrameworkElement) || previousVisible != visible || previousUnavailable != unavailable;

    void Insert(FrameworkElement anchor, bool bottom)
    {
        var parent = Parent(anchor) as Panel ?? throw new InvalidOperationException("The theme host is not in a panel.");
        var index = parent.Children.IndexOf(anchor);
        if (index < 0) throw new InvalidOperationException("The theme host has changed.");
        var wrapper = new Grid();
        wrapper.Resources[typeof(Grid)] = new Style(typeof(Grid));
        wrapper.RowDefinitions.Add(new RowDefinition { Height = bottom ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        wrapper.RowDefinitions.Add(new RowDefinition { Height = bottom ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(wrapper, Grid.GetRow(anchor)); Grid.SetColumn(wrapper, Grid.GetColumn(anchor));
        Grid.SetRowSpan(wrapper, Grid.GetRowSpan(anchor)); Grid.SetColumnSpan(wrapper, Grid.GetColumnSpan(anchor));
        DockPanel.SetDock(wrapper, DockPanel.GetDock(anchor)); Panel.SetZIndex(wrapper, Panel.GetZIndex(anchor));
        _parent = parent; _anchor = anchor; _wrapper = wrapper; _index = index;
        parent.Children.RemoveAt(index);
        var slot = new Grid();
        slot.Resources[typeof(Grid)] = new Style(typeof(Grid));
        for (var i = 0; i <= Grid.GetRow(anchor); i++) slot.RowDefinitions.Add(new RowDefinition { Height = i == Grid.GetRow(anchor) ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        for (var i = 0; i <= Grid.GetColumn(anchor); i++) slot.ColumnDefinitions.Add(new ColumnDefinition { Width = i == Grid.GetColumn(anchor) ? new GridLength(1, GridUnitType.Star) : new GridLength(0) });
        Grid.SetRow(slot, bottom ? 0 : 1); slot.Children.Add(anchor); wrapper.Children.Add(slot);
        panel.SetDocked(true); Grid.SetRow(panel, bottom ? 1 : 0); Grid.SetColumn(panel, 0); wrapper.Children.Add(panel);
        if (bottom) BindingOperations.SetBinding(wrapper, UIElement.VisibilityProperty, new Binding("Visibility") { Source = anchor });
        parent.Children.Insert(index, wrapper);
    }

    public void Detach()
    {
        RemovePanel();
        if (_wrapper != null && _anchor != null && _parent != null)
        {
            var index = _parent.Children.IndexOf(_wrapper);
            if (Parent(_anchor) is Panel slot) slot.Children.Remove(_anchor);
            if (index >= 0) { _parent.Children.RemoveAt(index); _parent.Children.Insert(index, _anchor); }
            else if (!_parent.Children.Contains(_anchor)) _parent.Children.Insert(Math.Min(_index, _parent.Children.Count), _anchor);
        }
        _wrapper = null; _anchor = null; _parent = null;
    }

    void RemovePanel()
    {
        switch (Parent(panel))
        {
            case Panel parent: parent.Children.Remove(panel); break;
            case ContentControl content when ReferenceEquals(content.Content, panel): content.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
            case null: break;
            default: throw new InvalidOperationException("Chat has an unsupported parent.");
        }
    }

    bool Fallback(string status, bool show, string? detail = null)
    {
        if (_lastFailure != status) { log($"{status} {detail}"); _lastFailure = status; }
        try { Recover(Detach, () => { RemovePanel(); Float(show, status); }, e => log($"fallback detach: {SafeDiagnostics.Error(e)}")); }
        catch (Exception e) { log($"floating fallback: {SafeDiagnostics.Error(e)}"); Status = status; Floating = true; }
        return false;
    }

    public static void Recover(Action detach, Action floating, Action<Exception> report)
    {
        try { detach(); } catch (Exception e) { report(e); }
        floating();
    }

    void Float(bool show, string status)
    {
        panel.SetDocked(false); Grid.SetRow(panel, 0); Grid.SetColumn(panel, 0);
        window.Content = panel; Floating = true; Status = status;
        if (show) { if (thisShow != null) thisShow(window); else window.Show(); }
    }

    public static DependencyObject? Parent(FrameworkElement element) => element.Parent ?? LogicalTreeHelper.GetParent(element) ?? VisualTreeHelper.GetParent(element);

    public static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>(); var seen = new HashSet<DependencyObject>(); var clock = Stopwatch.StartNew();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add(item)) continue;
            if (seen.Count > 4096 || clock.ElapsedMilliseconds > 20) throw new InvalidOperationException("Theme walk exceeded its budget.");
            yield return item;
            if (item is AssistantPanel) continue;
            foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>()) queue.Enqueue(child);
            if (item is Visual)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++) queue.Enqueue(VisualTreeHelper.GetChild(item, i));
        }
    }
}

public sealed record PlacementHost(bool Available, FrameworkElement? Main, object? Details, bool DetailsVisible);

public sealed class PlacementDriver(PanelPlacement placement, Func<PlacementHost> host, Func<long>? milliseconds = null)
{
    readonly Func<long> _clock = milliseconds ?? (() => Environment.TickCount64);
    PlacementHost? _last;
    long _settleUntil;
    bool _waiting, _suppressFallback;
    public bool Opened { get; private set; }
    public bool Shown => Opened && !(placement.Floating && _suppressFallback);
    public string Chosen { get; private set; } = "right";
    public bool HostUnavailable { get; private set; }
    public int PlaceCalls { get; private set; }
    public void Open(string chosen, bool startup)
    {
        Chosen = chosen; Opened = true; _suppressFallback = false;
        _settleUntil = startup ? _clock() + 1500 : 0; Refresh(true);
    }
    public void CloseFloating() { if (Chosen == "floating") Opened = false; else _suppressFallback = true; }
    public void Stop() { Opened = false; }
    public void Refresh(bool force = false)
    {
        if (!Opened) return;
        var next = host(); next = next with { Details = Chosen == "right" ? next.Details as FrameworkElement : null, DetailsVisible = Chosen == "right" && next.DetailsVisible };
        HostUnavailable = !next.Available;
        if (((!next.Available && Chosen != "floating") || (Chosen == "right" && next.Details == null)) && _clock() < _settleUntil) { _waiting = true; return; }
        if (!force && !_waiting && _last == next) return;
        _last = next; _waiting = false; PlaceCalls++;
        placement.Place(next.Available ? Chosen : "floating", next.Main, next.Details as FrameworkElement, next.DetailsVisible, !_suppressFallback);
    }
}
