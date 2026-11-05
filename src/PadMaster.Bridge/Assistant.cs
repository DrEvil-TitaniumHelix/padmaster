using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Speech.Recognition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Windows.Media;

namespace PadMaster;

public static class AssistantLook
{
    public static readonly Brush Background = Brush("#232429"), Surface = Brush("#2A2B34"), Foreground = Brush("#E1E1E1"), Dim = Brush("#B0B5C4"), Accent = Brush("#536BA0");
    public static Brush Brush(string value) { var brush = (Brush)new BrushConverter().ConvertFromString(value)!; brush.Freeze(); return brush; }
    public static TextBlock Text(string text, double size = 13, Brush? color = null) => new() { Text = text, FontSize = size, Foreground = color ?? Foreground, TextWrapping = TextWrapping.Wrap };
    public static Button Button(string text, string? tip = null) => new() { Content = text, ToolTip = tip, Padding = new Thickness(10, 6, 10, 6), MinHeight = 30, Cursor = Cursors.Hand };
    public static T Id<T>(T element, string id) where T : UIElement { AutomationProperties.SetAutomationId(element, id); return element; }

    public static void Apply(FrameworkElement element)
    {
        element.DataContext = new object();
        foreach (var type in new[] { typeof(Grid), typeof(StackPanel), typeof(Border), typeof(TextBlock), typeof(ScrollViewer), typeof(UserControl), typeof(Window) })
            element.Resources[type] = new Style(type);
        var resources = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style TargetType="Button">
                <Setter Property="Background" Value="#353844"/><Setter Property="Foreground" Value="#E1E1E1"/>
                <Setter Property="BorderBrush" Value="#484D5D"/><Setter Property="BorderThickness" Value="1"/>
                <Setter Property="FontSize" Value="12"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                  <Border x:Name="Shell" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Shell" Property="BorderBrush" Value="#9AAED8"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Shell" Property="BorderBrush" Value="#CEDBFA"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Shell" Property="Opacity" Value="0.5"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Background" Value="#1F2027"/><Setter Property="Foreground" Value="#E1E1E1"/>
                <Setter Property="CaretBrush" Value="#E1E1E1"/><Setter Property="SelectionBrush" Value="#536BA0"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="BorderBrush" Value="#505767"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="9,7"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}"><ScrollViewer x:Name="PART_ContentHost"/></Border>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Background" Value="#2A2B34"/><Setter Property="Foreground" Value="#E1E1E1"/><Setter Property="Padding" Value="10,7"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
                  <Border x:Name="Item" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}"><ContentPresenter/></Border>
                  <ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="#536BA0"/></Trigger></ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ComboBox">
                <Setter Property="Foreground" Value="#E1E1E1"/><Setter Property="MinHeight" Value="36"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox">
                  <Grid>
                    <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                      <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="#353844" BorderBrush="#505767" BorderThickness="1" CornerRadius="6"/></ControlTemplate></ToggleButton.Template>
                    </ToggleButton>
                    <ContentPresenter Content="{TemplateBinding SelectionBoxItem}" Margin="10,7,30,7" VerticalAlignment="Center" IsHitTestVisible="False"/>
                    <TextBlock Text="⌄" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="10" IsHitTestVisible="False"/>
                    <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
                      <Border Background="#2A2B34" BorderBrush="#505767" BorderThickness="1" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"><ScrollViewer><ItemsPresenter/></ScrollViewer></Border>
                    </Popup>
                  </Grid>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="CheckBox"><Setter Property="Foreground" Value="#E1E1E1"/><Setter Property="FontSize" Value="13"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
            </ResourceDictionary>
            """);
        foreach (var key in resources.Keys) element.Resources[key] = resources[key];
        if (element is Control control) { control.FontFamily = new FontFamily("Poppins, Segoe UI"); control.Foreground = Foreground; control.Background = Background; }
    }
}

public sealed class AssistantPanel : UserControl
{
    readonly Grid _body = new();
    readonly StackPanel _turns = new() { Margin = new Thickness(10, 5, 10, 5) };
    readonly ScrollViewer _scroll;
    readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 92, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "Message your assistant. Enter sends; Shift+Enter adds a line." };
    readonly TextBlock _status = AssistantLook.Text("offline", 11, AssistantLook.Dim);
    readonly TextBlock _activity = AssistantLook.Text("", 11, AssistantLook.Dim);
    readonly Button _send = AssistantLook.Button("Send"), _mic = AssistantLook.Button("\uE720", "mic unavailable"), _expand = AssistantLook.Button("⌃", "Collapse chat");
    readonly Grid _composer = new() { Margin = new Thickness(10, 0, 10, 10) };
    readonly ScrollViewer _setup = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly ComboBox _yourAi = new() { ItemsSource = new[] { "Claude Code", "Codex" }, SelectedIndex = -1 };
    readonly TextBox _where = new() { IsReadOnly = true, MinHeight = 34 };
    readonly Button _browse = AssistantLook.Button("Browse"), _signIn = AssistantLook.Button("Sign in");
    readonly TextBlock _setupMessage = AssistantLook.Text("", 12, AssistantLook.Dim);
    readonly TextBox _port = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
    readonly ComboBox _placement = new() { ItemsSource = new[] { "Right panel", "Left sidebar", "Floating window" }, MinHeight = 30 };
    readonly Border _bulkBar = new() { Visibility = Visibility.Collapsed, Background = AssistantLook.Brush("#4D411C"), BorderBrush = AssistantLook.Brush("#E6BC54"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(10, 0, 10, 6) };
    readonly CheckBox _confirmBulk = new() { Content = "Confirm bulk changes" };
    readonly TextBox _bulkThreshold = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), Width = 60 };
    readonly CheckBox _autoOpen = new() { Content = "Open at startup" };
    readonly Button _back = AssistantLook.Button("Back to chat");
    readonly Button _stop = AssistantLook.Button("Stop", "Stop this turn"), _retry = AssistantLook.Button("Retry", "Send that message again");
    readonly TextBlock _retryNote = AssistantLook.Text("this may repeat a change it already made", 11, AssistantLook.Dim);
    readonly HashSet<string> _dirtyFields = [];
    void Dirty(string field) { if (!_updatingSetup) { _dirtyFields.Add(field); _dirty = true; } }
    Settings _cardSettings = new();
    SetupState _setupState = new("", null, false);
    TextBlock? _reply;
    Microphone? _microphone;
    Brush _accent = AssistantLook.Accent;
    bool _dock = true, _expanded = true, _ready, _busy, _disposed, _updatingSetup, _needsSetup, _greeted, _waiting, _editingSetup, _dirty, _announced, _firstUpdate;
    readonly int _createdThread = Environment.CurrentManagedThreadId;
    Action<string>? _logSink;
    long _stateStamp;
    public Action? StopRequested { get; set; }
    public Action? RetryRequested { get; set; }
    public bool Busy => _busy;
    public bool SendEnabled => _send.IsEnabled;
    public bool CanRetry => _retry.Visibility == Visibility.Visible && _retry.IsEnabled;
    public bool CanStop => _stop.Visibility == Visibility.Visible;
    public string Notes => string.Join("\n", _turns.Children.OfType<TextBlock>().Select(line => line.Text));
    public Action<string>? SendRequested { get; set; }
    public Action? SettingsRequested { get; set; }
    public Action? HelpRequested { get; set; }
    public Action? Reopened { get; set; }
    public Action<IReadOnlyDictionary<string, string?>, bool>? ConfigurationSaved { get; set; }
    public Action<bool>? ExpandedChanged { get; set; }
    public Action<string>? Log
    {
        get => _logSink;
        set
        {
            _logSink = value;
            if (value == null || _announced) return;
            _announced = true;
            value($"panel created: thread={_createdThread}, host dispatcher={ReferenceEquals(Application.Current?.Dispatcher, Dispatcher)}, access={Dispatcher.CheckAccess()}");
        }
    }
    public bool SetupVisible => _setup.Visibility == Visibility.Visible;
    public bool Expanded => _expanded;
    public string Draft => _input.Text;
    public bool FocusInput() => _input.Focus();
    public string Transcript => string.Join("\n", _turns.Children.OfType<Border>().Select(x => ((TextBlock)x.Child).Text));

    public AssistantPanel() : this(true) { }

    public AssistantPanel(bool probeMicrophone)
    {
        AssistantLook.Apply(this);
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        var root = new Grid { Background = AssistantLook.Background };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Background = AssistantLook.Surface, Margin = new Thickness(0, 0, 0, 1) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = AssistantLook.Text("Padmaster AI Assistant", 13);
        title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center; title.Margin = new Thickness(10, 0, 4, 0); header.Children.Add(title);
        _mic.FontFamily = new FontFamily("Segoe MDL2 Assets"); _mic.FontSize = 15;
        _mic.Click += (_, _) => ToggleMic(); _mic.IsEnabled = false;
        var gear = AssistantLook.Id(AssistantLook.Button("\uE713", "Configure"), "PadGear"); gear.FontFamily = new FontFamily("Segoe MDL2 Assets"); gear.Click += (_, _) => SettingsRequested?.Invoke();
        _expand.Click += (_, _) => ToggleExpanded();
        var buttons = new[] { _mic, gear, _expand };
        for (var i = 0; i < buttons.Length; i++) { buttons[i].Padding = new Thickness(7, 4, 7, 4); buttons[i].Margin = new Thickness(1, 4, 3, 4); Grid.SetColumn(buttons[i], i + 1); header.Children.Add(buttons[i]); }
        root.Children.Add(header);
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _body.RowDefinitions.Add(new RowDefinition());
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _status.Margin = new Thickness(10, 7, 10, 3);
        _body.Children.Add(_status);
        _scroll = new ScrollViewer { Content = _turns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AssistantLook.Id(_scroll, "PadTranscript"); AssistantLook.Id(_input, "PadInput"); AssistantLook.Id(_send, "PadSend"); AssistantLook.Id(_mic, "PadMic"); AssistantLook.Id(_status, "PadStatus");
        var userScrollUntil = DateTime.MinValue;
        _scroll.PreviewMouseWheel += (_, _) => userScrollUntil = DateTime.UtcNow.AddSeconds(1);
        _scroll.PreviewMouseDown += (_, _) => userScrollUntil = DateTime.UtcNow.AddSeconds(1);
        _scroll.PreviewKeyDown += (_, _) => userScrollUntil = DateTime.UtcNow.AddSeconds(1);
        _scroll.ScrollChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, _scroll) || e.VerticalOffset != 0 || e.VerticalChange >= 0 || e.ExtentHeightChange != 0 || DateTime.UtcNow < userScrollUntil || Mouse.LeftButton == MouseButtonState.Pressed) return;
            var previous = -e.VerticalChange;
            _ = Dispatcher.BeginInvoke(() => { if (!_disposed) _scroll.ScrollToVerticalOffset(previous); }, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        Grid.SetRow(_scroll, 1); _body.Children.Add(_scroll);
        var turnBar = new Grid { Margin = new Thickness(12, 2, 12, 5) };
        turnBar.ColumnDefinitions.Add(new ColumnDefinition()); turnBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); turnBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _activity.VerticalAlignment = VerticalAlignment.Center; turnBar.Children.Add(AssistantLook.Id(_activity, "PadActivity"));
        foreach (var (button, column, id) in new[] { (_stop, 1, "PadStop"), (_retry, 2, "PadRetry") })
        {
            button.Padding = new Thickness(8, 2, 8, 2); button.MinHeight = 24; button.FontSize = 11; button.Margin = new Thickness(5, 0, 0, 0);
            button.Visibility = Visibility.Collapsed; Grid.SetColumn(button, column); turnBar.Children.Add(AssistantLook.Id(button, id));
        }
        _stop.Click += (_, _) => StopRequested?.Invoke();
        _retry.Click += (_, _) => RetryRequested?.Invoke();
        Grid.SetRow(turnBar, 2); _body.Children.Add(turnBar);
        _retryNote.Margin = new Thickness(12, 0, 12, 4); _retryNote.Visibility = Visibility.Collapsed; Grid.SetRow(_retryNote, 2); _body.Children.Add(_retryNote);
        var composer = _composer;
        composer.ColumnDefinitions.Add(new ColumnDefinition()); composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        composer.Children.Add(_input);
        _send.Margin = new Thickness(7, 0, 0, 0); _send.Background = AssistantLook.Accent; _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.Click += (_, _) => Send(); Grid.SetColumn(_send, 1); composer.Children.Add(_send);
        _body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_bulkBar, 3); _body.Children.Add(_bulkBar);
        Grid.SetRow(composer, 4); _body.Children.Add(composer);
        BuildSetup(); Grid.SetRow(_setup, 1); Grid.SetRowSpan(_setup, 4); _body.Children.Add(_setup);
        Grid.SetRow(_body, 1); root.Children.Add(_body); Content = root;
        _input.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Send(); }
        };
        var seen = 0;
        _input.GotKeyboardFocus += (_, _) => { if ((seen & 1) == 0) { seen |= 1; Log?.Invoke($"input focused in {Window.GetWindow(this)?.GetType().Name ?? "no window"}"); } };
        _input.AddHandler(Keyboard.KeyUpEvent, new KeyEventHandler((_, e) => e.Handled = true), true);
        _input.AddHandler(TextCompositionManager.TextInputEvent, new TextCompositionEventHandler((_, e) => { e.Handled = true; if ((seen & 2) == 0) { seen |= 2; Log?.Invoke("input received text"); } }), true);
        Unloaded += (_, _) => StopMic();
        Loaded += (_, _) => { if (_microphone is { Available: true } && _setup.Visibility != Visibility.Visible) _mic.IsEnabled = true; };
        SetDocked(true); SetReady(false); SetSetup(_setupState with { Checking = true }); _needsSetup = false;
        if (probeMicrophone) _ = ProbeMicrophone();
    }

    void BuildSetup()
    {
        var card = new StackPanel { Margin = new Thickness(12, 2, 12, 6) };
        var choice = new DockPanel(); var heading = AssistantLook.Text("Your AI", 13); heading.Width = 65; heading.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(heading, Dock.Left); choice.Children.Add(heading); choice.Children.Add(_yourAi); card.Children.Add(choice);
        void Label(string text) { var label = AssistantLook.Text(text, 12, AssistantLook.Dim); label.Margin = new Thickness(0, 4, 0, 2); card.Children.Add(label); }
        _yourAi.SelectionChanged += (_, _) => { Dirty("assistant"); if (!_updatingSetup) { _where.Text = ""; Dirty("cli"); } };
        Label("Where it is");
        var path = new DockPanel(); _browse.Margin = new Thickness(7, 0, 0, 0); DockPanel.SetDock(_browse, Dock.Right); path.Children.Add(_browse); path.Children.Add(_where); card.Children.Add(path);
        _browse.Click += (_, _) =>
        {
            try
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Title = "Find " + (_yourAi.SelectedIndex == 1 ? "Codex" : "Claude Code"), Filter = "Assistant CLI (*.exe;*.cmd)|*.exe;*.cmd", CheckFileExists = true };
                if (picker.ShowDialog(Window.GetWindow(this)) == true) { _where.Text = picker.FileName; Dirty("cli"); }
            }
            catch (Exception e) { Log?.Invoke($"CLI browse: {SafeDiagnostics.Error(e)}"); SetSetupMessage("The file picker could not open. Enter the path here or in Configure."); }
        };
        _where.IsReadOnly = false; _where.MinHeight = 30; _where.Padding = new Thickness(6, 4, 6, 4); _where.TextChanged += (_, _) => Dirty("cli");
        _yourAi.MinHeight = 30; _yourAi.Height = 30;
        var options = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        options.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) }); options.ColumnDefinitions.Add(new ColumnDefinition());
        var portColumn = new StackPanel { Margin = new Thickness(0, 0, 8, 0) }; portColumn.Children.Add(AssistantLook.Text("Port", 11, AssistantLook.Dim)); portColumn.Children.Add(_port); options.Children.Add(portColumn);
        var placementColumn = new StackPanel(); placementColumn.Children.Add(AssistantLook.Text("Placement", 11, AssistantLook.Dim)); placementColumn.Children.Add(_placement); Grid.SetColumn(placementColumn, 1); options.Children.Add(placementColumn); card.Children.Add(options);
        _port.TextChanged += (_, _) => Dirty("port");
        _placement.SelectionChanged += (_, _) => Dirty("placement");
        _autoOpen.Click += (_, _) => Dirty("autoOpen");
        _autoOpen.Margin = new Thickness(0, 7, 0, 0); card.Children.Add(_autoOpen);
        var bulk = new DockPanel { Margin = new Thickness(0, 7, 0, 0) };
        var threshold = new StackPanel { Orientation = Orientation.Horizontal };
        var label = AssistantLook.Text("Bulk threshold", 11, AssistantLook.Dim); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(5, 0, 5, 0); threshold.Children.Add(label); threshold.Children.Add(_bulkThreshold);
        DockPanel.SetDock(threshold, Dock.Right); bulk.Children.Add(threshold); _confirmBulk.VerticalAlignment = VerticalAlignment.Center; bulk.Children.Add(_confirmBulk); card.Children.Add(bulk);
        _confirmBulk.Click += (_, _) => Dirty("confirmBulk"); _bulkThreshold.TextChanged += (_, _) => Dirty("bulkThreshold");
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        _signIn.Background = AssistantLook.Accent; _signIn.Click += (_, _) => { if (!_waiting) SaveConfiguration(true); };
        var help = AssistantLook.Button("Help"); help.Margin = new Thickness(8, 0, 0, 0); help.Click += (_, _) => HelpRequested?.Invoke();
        var save = AssistantLook.Button("Save"); save.Margin = new Thickness(6, 0, 0, 0); save.Click += (_, _) => SaveConfiguration(false);
        _back.Margin = new Thickness(6, 0, 0, 0); _back.Click += (_, _) => { LoadConfiguration(_cardSettings, true); _editingSetup = false; SetSetup(_setupState); };
        actions.Children.Add(_signIn); actions.Children.Add(help); actions.Children.Add(save); actions.Children.Add(_back); card.Children.Add(actions);
        _setupMessage.Margin = new Thickness(0, 5, 0, 0); card.Children.Add(_setupMessage); _setup.Content = card;
        LoadConfiguration(new());
    }

    public void LoadConfiguration(Settings settings, bool discardEdits = false)
    {
        if (discardEdits) _dirtyFields.Clear();
        _cardSettings = settings; _updatingSetup = true;
        if (!_dirtyFields.Contains("assistant")) _yourAi.SelectedIndex = settings.Assistant == "claude-code" ? 0 : settings.Assistant == "codex" ? 1 : -1;
        if (!_dirtyFields.Contains("cli")) _where.Text = settings.Cli.Length == 0 ? _setupState.Cli ?? "" : settings.Cli;
        if (!_dirtyFields.Contains("port")) _port.Text = settings.Port.ToString();
        if (!_dirtyFields.Contains("placement")) _placement.SelectedIndex = settings.Placement == "left" ? 1 : settings.Placement == "floating" ? 2 : 0;
        if (!_dirtyFields.Contains("autoOpen")) _autoOpen.IsChecked = settings.AutoOpen;
        if (!_dirtyFields.Contains("confirmBulk")) _confirmBulk.IsChecked = settings.ConfirmBulk;
        if (!_dirtyFields.Contains("bulkThreshold")) _bulkThreshold.Text = settings.BulkThreshold.ToString();
        _updatingSetup = false; _dirty = _dirtyFields.Count > 0;
    }
    public void OpenConfiguration() { _editingSetup = true; SetExpanded(true); SetSetup(_setupState); }
    public void ConfigurationAccepted(Settings settings) { LoadConfiguration(settings, true); _editingSetup = false; SetSetup(_setupState); }
    void SaveConfiguration(bool signIn)
    {
        var fields = new Dictionary<string, string?> { ["assistant"] = _yourAi.SelectedIndex == 0 ? "claude-code" : _yourAi.SelectedIndex == 1 ? "codex" : "", ["cli"] = _cardSettings.Cli.Length == 0 && _where.Text == _setupState.Cli ? "" : _where.Text,
            ["port"] = _port.Text, ["placement"] = _placement.SelectedIndex == 1 ? "left" : _placement.SelectedIndex == 2 ? "floating" : "right", ["autoOpen"] = (_autoOpen.IsChecked == true).ToString(), ["confirmBulk"] = (_confirmBulk.IsChecked == true).ToString(), ["bulkThreshold"] = _bulkThreshold.Text };
        if (!Settings.TryApply(_cardSettings, fields, false, out _, out var error)) { SetSetupMessage(error); return; }
        ConfigurationSaved?.Invoke(fields, signIn);
    }

    public void SetSetup(SetupState state)
    {
        _setupState = state;
        _updatingSetup = true;
        if (!_dirtyFields.Contains("assistant")) _yourAi.SelectedIndex = state.Assistant == "claude-code" ? 0 : state.Assistant == "codex" ? 1 : -1;
        if (!_dirtyFields.Contains("cli")) _where.Text = _cardSettings.Cli.Length == 0 ? state.Cli ?? "" : _cardSettings.Cli;
        _where.ToolTip = state.Cli; _updatingSetup = false;
        var show = !state.Suppressed && !state.Checking && (state.NeedsSetup || _editingSetup || _dirty);
        _setup.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = _activity.Visibility = _composer.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _mic.Visibility = show ? Visibility.Collapsed : Visibility.Visible; _back.Visibility = state.Ready ? Visibility.Visible : Visibility.Collapsed;
        if (show) StopMic();
        if (state.NeedsSetup && !_needsSetup && !_expanded) { SetExpanded(true); }
        _needsSetup = state.NeedsSetup;
        if (!_waiting) SetSetupMessage(state.Reason.Length > 0 ? state.Reason : state.Assistant is not ("claude-code" or "codex") ? "Choose your AI to get started." : state.Cli == null ? $"Install {Settings.AssistantName(state.Assistant)}, then choose its file. Help has the steps." : state.Assistant == "codex" ? "Use your own ChatGPT subscription." : "Use your own Claude Code subscription.");
        SetSetupWaiting(_waiting);
        if (state.Ready && !_greeted)
        {
            _greeted = true; AddTurn("Hi, I’m Padmaster. Ask me about your games or controls.", false); _reply = null;
        }
    }

    public void SetSetupMessage(string text) => _setupMessage.Text = text;
    public void SetSetupWaiting(bool waiting)
    {
        _waiting = waiting; _signIn.IsEnabled = !waiting && !_setupState.Suppressed;
        _yourAi.IsEnabled = _browse.IsEnabled = _where.IsEnabled = _port.IsEnabled = _placement.IsEnabled = _autoOpen.IsEnabled = _confirmBulk.IsEnabled = _bulkThreshold.IsEnabled = !waiting;
    }

    async Task ProbeMicrophone()
    {
        var microphone = await Task.Run(() => Microphone.Probe(text => Ui(() => { _input.AppendText((_input.Text.Length == 0 ? "" : " ") + text); _input.CaretIndex = _input.Text.Length; }), reason => Ui(() => MicFailed(reason))));
        if (_disposed) { microphone.Dispose(); return; }
        SetMicrophone(microphone);
    }

    public void SetMicrophone(Microphone microphone)
    {
        _microphone?.Dispose(); _microphone = microphone;
        _mic.IsEnabled = microphone.Available; _mic.ToolTip = microphone.Available ? "Start dictation" : "mic unavailable";
        if (!microphone.Available) Log?.Invoke("mic unavailable: recognizer unavailable");
    }

    void ToggleMic()
    {
        try
        {
            _microphone?.Toggle();
            _mic.Background = _microphone?.Listening == true ? _accent : AssistantLook.Surface;
            _mic.ToolTip = _microphone?.Listening == true ? "Stop dictation" : "Start dictation";
        }
        catch (Exception e) { MicFailed(e.ToString()); }
    }

    void MicFailed(string reason)
    {
        Log?.Invoke("mic unavailable: recognizer stopped"); _mic.IsEnabled = false; _mic.ToolTip = "mic unavailable"; _mic.Background = AssistantLook.Surface;
        _microphone?.Dispose(); _microphone = null;
    }

    void StopMic()
    {
        try { if (_microphone?.Listening == true) _microphone.Toggle(); }
        catch (Exception e) { Log?.Invoke($"mic stop: {SafeDiagnostics.Error(e)}"); }
        finally
        {
            _mic.Background = AssistantLook.Surface;
            if (_microphone is { Available: true }) { _mic.ToolTip = "Start dictation"; _mic.IsEnabled = true; }
        }
    }

    public void SetDocked(bool docked) { _dock = docked; _expand.Visibility = docked ? Visibility.Visible : Visibility.Collapsed; Resize(); }
    public void UseTheme(FrameworkElement source)
    {
        if (source is ContentControl { Content: FrameworkElement content } && source is not UserControl) source = content;
        var probe = new Border();
        BindingOperations.SetBinding(probe, Border.BackgroundProperty, new Binding("SelectionBackgroundBrush") { Source = source.DataContext, Mode = BindingMode.OneTime });
        if (probe.Background is Brush accent)
        {
            _accent = accent.CloneCurrentValue(); _send.Background = _signIn.Background = _accent; _input.SelectionBrush = _accent;
            foreach (var bubble in _turns.Children.OfType<Border>().Where(x => x.Tag is true)) bubble.Background = _accent;
        }
        if (source is Control control) FontFamily = control.FontFamily;
    }
    public void ToggleExpanded() { SetExpanded(!_expanded); ExpandedChanged?.Invoke(_expanded); if (_expanded) Reopened?.Invoke(); }
    public void SetExpanded(bool expanded) { _expanded = expanded; Resize(); }
    void Resize()
    {
        _body.Visibility = !_dock || _expanded ? Visibility.Visible : Visibility.Collapsed;
        Height = _dock ? (_expanded ? 360 : 40) : double.NaN;
        _expand.Content = _expanded ? "⌃" : "⌄"; _expand.ToolTip = _expanded ? "Collapse chat" : "Expand chat";
    }
    public void ShowBulk(BulkPrompt prompt)
    {
        SetExpanded(true); _editingSetup = false; _setup.Visibility = Visibility.Collapsed;
        _scroll.Visibility = _activity.Visibility = _composer.Visibility = Visibility.Visible;
        var content = new StackPanel();
        content.Children.Add(AssistantLook.Text($"The assistant wants to {prompt.Summary} ({prompt.Count?.ToString() ?? "unknown number of"} items).", 12, AssistantLook.Brush("#FFE6A0")));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var yes = AssistantLook.Id(AssistantLook.Button("Yes, do it"), "PadConfirmYes"); var no = AssistantLook.Id(AssistantLook.Button("No"), "PadConfirmNo"); no.Margin = new Thickness(7, 0, 0, 0);
        void Answer(bool accept) { yes.IsEnabled = no.IsEnabled = false; _bulkBar.Visibility = Visibility.Collapsed; prompt.Answer(accept); }
        yes.Click += (_, _) => Answer(true); no.Click += (_, _) => Answer(false);
        buttons.Children.Add(yes); buttons.Children.Add(no); content.Children.Add(buttons); _bulkBar.Child = content; _bulkBar.Visibility = Visibility.Visible;
    }
    public void BulkFinished(string text, bool dismiss = true) { if (dismiss) _bulkBar.Visibility = Visibility.Collapsed; SetActivity(text); }
    public void BeginHiddenTurn() { _reply = null; _busy = true; SetReady(_ready); }
    public void ApplyState(TurnState state)
    {
        if (state.Stamp <= _stateStamp) return;
        _stateStamp = state.Stamp;
        _busy = state.Busy;
        _stop.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;
        _retry.Visibility = state.CanRetry ? Visibility.Visible : Visibility.Collapsed;
        _retry.IsEnabled = state.CanRetry && !state.Restarting;
        _retryNote.Visibility = state.CanRetry && state.UsedWrites ? Visibility.Visible : Visibility.Collapsed;
        if (state.Notice.Length > 0) ShowFailure(state.Notice);
        else if (!state.Busy && _activity.Text == "working…") _activity.Text = "";
        SetReady(_ready);
    }
    public void SetStatus(string text) => _status.Text = text;
    public void SetReady(bool ready) { _ready = ready; _send.IsEnabled = ready; }
    public void AddNote(string text)
    {
        var note = AssistantLook.Text(text, 11, AssistantLook.Dim);
        note.Margin = new Thickness(26, 0, 4, 4); note.TextAlignment = TextAlignment.Right;
        _turns.Children.Add(note); Scroll();
    }
    public void AddTurn(string text, bool user)
    {
        var block = AssistantLook.Text(text);
        var bubble = new Border { Child = block, Tag = user, Background = user ? _accent : AssistantLook.Surface,
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(user ? 24 : 0, 4, user ? 0 : 24, 4),
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        _turns.Children.Add(bubble);
        if (!user) _reply = block;
        Scroll();
    }
    public void SetReply(string text) { if (_reply == null) AddTurn(text, false); else _reply.Text = text; Scroll(); }
    public void SetActivity(string text) { _activity.Text = text; _activity.Opacity = 1; }
    public void Finish(bool success) { _busy = false; _reply = null; if (success) _activity.Text = ""; _activity.Opacity = .8; SetReady(_ready); }
    public void ShowFailure(string text) { _activity.Text = text; _activity.Opacity = .85; }
    public void Ui(Action action)
    {
        if (_disposed) { Log?.Invoke("panel update dropped: the panel is disposed"); return; }
        if (Dispatcher.HasShutdownStarted) { Log?.Invoke("panel update dropped: the window is closing"); return; }
        void Safe()
        {
            if (_disposed) { Log?.Invoke("panel update dropped: the panel is disposed"); return; }
            if (!_firstUpdate) { _firstUpdate = true; Log?.Invoke($"first panel update: thread={Environment.CurrentManagedThreadId}, created on={_createdThread}, access={Dispatcher.CheckAccess()}"); }
            try { action(); } catch (Exception e) { Log?.Invoke($"panel update failed: {SafeDiagnostics.Error(e)}"); }
        }
        try { if (Dispatcher.CheckAccess()) Safe(); else _ = Dispatcher.BeginInvoke(Safe); } catch (Exception e) { Log?.Invoke($"panel update dropped: dispatch failed, {SafeDiagnostics.Error(e)}"); }
    }
    void Scroll() { if (!_disposed) _ = Dispatcher.BeginInvoke(() => _scroll.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Loaded); }
    void Send()
    {
        SubmitText(_input.Text);
    }
    public void SubmitText(string input)
    {
        var text = input.Trim();
        if (text.StartsWith('/')) { ShowFailure("Commands aren't available here"); return; }
        if (!_ready || text.Length == 0) return;
        var queued = _busy;
        _busy = true; SetReady(_ready); AddTurn(text, true); _input.Clear();
        if (queued) AddNote("queued"); else { _reply = null; SetActivity("working…"); }
        try { SendRequested?.Invoke(text); } catch (Exception e) { Log?.Invoke($"send: {SafeDiagnostics.Error(e)}"); ShowFailure("The assistant is unavailable. Please try again."); Finish(false); }
    }
    public void Shutdown() { _disposed = true; _microphone?.Dispose(); }
    public void Suspend() { StopMic(); SetReady(false); }
    public void Idle() { _busy = false; _reply = null; _stop.Visibility = _retry.Visibility = _retryNote.Visibility = Visibility.Collapsed; if (_activity.Text == "working…") _activity.Text = ""; SetReady(_ready); }
}

public sealed class AssistantWindow : Window
{
    bool _shutdown;
    public event Action? UserClosed;
    public AssistantWindow()
    {
        AssistantLook.Apply(this); Title = "Padmaster AI Assistant"; Width = 520; Height = 720; MinWidth = 300; MinHeight = 360;
        Topmost = false; ShowActivated = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Closing += (_, e) => { if (!_shutdown && !Dispatcher.HasShutdownStarted && Application.Current?.Dispatcher.HasShutdownStarted != true) { e.Cancel = true; Hide(); UserClosed?.Invoke(); } };
    }
    public void Shutdown() { _shutdown = true; Close(); }
}

public sealed class Microphone : IDisposable
{
    public bool Available { get; private init; }
    public string Reason { get; private init; } = "";
    public bool Listening { get; private set; }
    Action<bool>? _toggle;
    Action? _dispose;
    public static Microphone Injected(Action<bool> toggle, Action? dispose = null, bool available = true) => new() { Available = available, _toggle = toggle, _dispose = dispose };
    public static Microphone Probe(Action<string>? recognized = null, Action<string>? failed = null)
    {
        try { return OpenEngine(recognized ?? (_ => { }), failed ?? (_ => { })); }
        catch (Exception e) { return new() { Reason = e.GetBaseException().Message }; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Microphone OpenEngine(Action<string> recognized, Action<string> failed)
    {
        SpeechRecognitionEngine? engine = null;
        try
        {
            var installed = SpeechRecognitionEngine.InstalledRecognizers();
            var info = installed.FirstOrDefault(x => x.Culture.Equals(System.Globalization.CultureInfo.CurrentUICulture)) ?? installed.FirstOrDefault();
            if (info == null) return new() { Reason = "No Windows speech recognizer is installed." };
            engine = new SpeechRecognitionEngine(info);
            engine.LoadGrammar(new DictationGrammar()); engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += (_, e) => recognized(e.Result.Text);
            engine.RecognizeCompleted += (_, e) => { if (e.Error != null) failed(e.Error.ToString()); };
            var ready = engine;
            return new() { Available = true, _toggle = listen => { if (listen) ready.RecognizeAsync(RecognizeMode.Multiple); else ready.RecognizeAsyncCancel(); }, _dispose = ready.Dispose };
        }
        catch { engine?.Dispose(); throw; }
    }
    public void Toggle() { if (!Available) return; var next = !Listening; _toggle?.Invoke(next); Listening = next; }
    public void Dispose() { Listening = false; try { _dispose?.Invoke(); } catch { } _dispose = null; _toggle = null; }
}

public sealed class ChatBridge
{
    readonly Session _session;
    readonly AssistantPanel _panel;
    readonly Func<Session?> _current;
    readonly Func<bool> _stopping;
    readonly Action<string> _log;
    readonly HashSet<string> _drops = [];
    int _shownReply;
    public ChatBridge(Session session, AssistantPanel panel, Func<Session?> current, Func<bool> stopping, Action<string> log, Action<bool>? afterDone = null)
    {
        _session = session; _panel = panel; _current = current; _stopping = stopping; _log = log;
        session.Reply += text => Ui(() => { _shownReply = session.ReplyRevision; panel.SetReply(text); });
        session.Activity += text => Ui(() => panel.SetActivity(text));
        session.Failure += text => Ui(() => panel.ShowFailure(text));
        session.HiddenTurn += () => Ui(panel.BeginHiddenTurn);
        session.StateChanged += state => Ui(() => panel.ApplyState(state));
        session.Done += success => Ui(() => { panel.Finish(success); afterDone?.Invoke(success); });
        panel.StopRequested = session.StopTurn;
        panel.RetryRequested = session.Retry;
    }
    public void Heal()
    {
        if (_stopping() || !ReferenceEquals(_current(), _session)) return;
        if (_session.ReplyRevision != _shownReply) { _shownReply = _session.ReplyRevision; _panel.SetReply(_session.LastReply); }
        _panel.ApplyState(_session.State);
    }
    void Ui(Action action)
    {
        if (_stopping()) return;
        _panel.Ui(() =>
        {
            if (!ReferenceEquals(_current(), _session)) { Drop("session mismatch"); return; }
            if (_stopping()) return;
            action();
        });
    }
    void Drop(string reason)
    {
        lock (_drops) if (!_drops.Add(reason)) return;
        _log("panel update dropped: " + reason);
    }
}
