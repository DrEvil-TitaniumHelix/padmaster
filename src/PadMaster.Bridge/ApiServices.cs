using System.Collections.Concurrent;
using Unbroken.LaunchBox.Plugins;
using Unbroken.LaunchBox.Plugins.Data;
using Unbroken.LaunchBox.Plugins.Services.Achievements;
using Unbroken.LaunchBox.Plugins.Services.Activity;
using Unbroken.LaunchBox.Plugins.Services.Archives;
using Unbroken.LaunchBox.Plugins.Services.GameData;
using Unbroken.LaunchBox.Plugins.Services.Leaderboards;
using Unbroken.LaunchBox.Plugins.Services.Logging;
using Unbroken.LaunchBox.Plugins.Services.Notifications;
using Unbroken.LaunchBox.Plugins.Services.Profiles;
using Unbroken.LaunchBox.Plugins.Services.Storage;

namespace PadMaster;

public sealed class ApiServices : ISystemEventsPlugin
{
    internal static readonly Type[] Types = [typeof(IAchievementManager), typeof(IPlayerProfileManager), typeof(IPlayerActivityManager), typeof(IGameDataManager), typeof(ILeaderboardManager), typeof(IArchiveExtractionManager), typeof(IPluginLogger), typeof(IPluginDataStore), typeof(IPluginFileSystemStore), typeof(INotificationManager)];
    internal static readonly ConcurrentDictionary<Type, object> Instances = new();
    public ApiServices(IAchievementManager achievements, IPlayerProfileManager profiles, IPlayerActivityManager activity, IGameDataManager gameData, ILeaderboardManager leaderboards, IArchiveExtractionManager archives, IPluginLogger logger, IPluginDataStore data, IPluginFileSystemStore files, INotificationManager notifications)
    {
        if (PluginHelper.BigBoxMainViewModel != null) return;
        object[] values = [achievements, profiles, activity, gameData, leaderboards, archives, logger, data, files, notifications];
        for (var i = 0; i < Types.Length; i++) Instances[Types[i]] = values[i];
    }
    public void OnEventRaised(string eventType) { if (eventType == SystemEventTypes.BigBoxStartupCompleted) Instances.Clear(); }
}
