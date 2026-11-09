using System.IO;
using System.Windows;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;

namespace PadMaster.Controls;

// The "PM" badge: this game has a controller mapping. Mappings are read once per LaunchBox session.
public sealed class PadMasterBadge(string pluginDirectory) : IGameBadge
{
    public PadMasterBadge() : this(Path.GetDirectoryName(typeof(PadMasterBadge).Assembly.Location)!) { }
    readonly Lazy<HashSet<string>> mapped = new(() => new(Directory.GetFiles(Path.Combine(pluginDirectory, "games"), "*.json", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase));
    public string Name => "Pad Master controller mapping";
    public string UniqueId => "PadMasterMapped";
    public int Index { get; set; }
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> Generated = new(StringComparer.OrdinalIgnoreCase);  // maps made since startup
    public bool GetAppliesToGame(IGame game) { if (!ControlsPlugin.IsEnabled(pluginDirectory)) return false; var path = Mapping.GamePath(pluginDirectory, game.Platform, game.Title); return mapped.Value.Contains(path) || Generated.ContainsKey(path); }
    public System.Drawing.Image DefaultIcon
    {
        get
        {
            var icon = new System.Drawing.Bitmap(64, 64);
            using var g = System.Drawing.Graphics.FromImage(icon); using var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0, 120)); using var font = new System.Drawing.Font("Segoe UI", 26, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit; g.FillEllipse(fill, 1, 1, 62, 62);
            g.DrawString("PM", font, System.Drawing.Brushes.White, new System.Drawing.RectangleF(0, 0, 64, 64), new System.Drawing.StringFormat { Alignment = System.Drawing.StringAlignment.Center, LineAlignment = System.Drawing.StringAlignment.Center });
            return icon;
        }
    }
}

// At LaunchBox / Big Box startup, publish artwork for every mapped game so a theme can show it while browsing.
public sealed class ArtworkSweepPlugin : ISystemEventsPlugin
{
    int started;
    public void OnEventRaised(string eventType)
    {
        if (eventType is not (SystemEventTypes.LaunchBoxStartupCompleted or SystemEventTypes.BigBoxStartupCompleted) || Interlocked.Exchange(ref started, 1) == 1) return;
        var dir = Path.GetDirectoryName(typeof(ArtworkSweepPlugin).Assembly.Location)!;
        if (!ControlsPlugin.IsEnabled(dir)) { RotatingLog.Write(Path.Combine(dir, "padmaster.log"), "artwork sweep: skipped, plugin not enabled (Configure > Enable)"); return; }
        Task.Run(() =>
        {
            try { Run(PluginHelper.DataManager.GetAllGames(), id => PluginHelper.DataManager.GetEmulatorById(id), dir, Path.GetFullPath(Path.Combine(dir, "..", "..")), () => Application.Current?.Dispatcher.BeginInvoke(() => PluginHelper.LaunchBoxMainViewModel?.RefreshData())); }
            catch (Exception e) { RotatingLog.Write(Path.Combine(dir, "padmaster.log"), "artwork sweep: error " + e.Message); }
        });
    }

    public static (int Mapped, int Written, int Skipped) Run(IEnumerable<IGame> games, Func<string, IEmulator?> findEmulator, string pluginDirectory, string launchBoxRoot, Action refresh)
    {
        var have = new HashSet<string>(Directory.GetFiles(Path.Combine(pluginDirectory, "games"), "*.json", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase);
        int mapped = 0, written = 0, skipped = 0;
        foreach (var game in games.Where(g => have.Contains(Mapping.GamePath(pluginDirectory, g.Platform, g.Title))))
        {
            mapped++;
            try
            {
                var resolved = ControlsPlugin.Resolve(game, findEmulator(game.EmulatorId), pluginDirectory, launchBoxRoot);
                var (artwork, error) = resolved == null ? (null, "no bindings") : ControlsPlugin.Publish(game, resolved, pluginDirectory, () => { });  // one refresh at the end, not one per game
                if (artwork == null) { skipped++; RotatingLog.Write(Path.Combine(pluginDirectory, "padmaster.log"), $"artwork sweep: {game.Title} skipped: {error}"); } else written += artwork.Writes;
            }
            catch (Exception e) { skipped++; RotatingLog.Write(Path.Combine(pluginDirectory, "padmaster.log"), $"artwork sweep: {game.Title} skipped: {e.Message}"); }
        }
        if (written > 0) refresh();
        RotatingLog.Write(Path.Combine(pluginDirectory, "padmaster.log"), $"artwork sweep: {mapped} mapped games, {written} pictures written, {skipped} skipped");
        return (mapped, written, skipped);
    }
}
