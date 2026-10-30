using System.Text.Json;
using ARI.Common;
using ARI.LLM;
using Microsoft.Extensions.Logging;

namespace ARI.Plugins.Discord;

/// <summary>
/// Discord as a plugin. It owns the connection to Discord (<see cref="DiscordModule"/>), the settings that go with it, and the five tools Ari
/// uses to act there. The control panel's Plugins page draws its card from <see cref="Settings"/>, so there is no Discord page of its own.
/// It is also the worked example of how to write a plugin.
/// </summary>
public sealed class DiscordPlugin : IPlugin, IToolProvider
{
    private readonly IPluginHost host;
    private readonly ILoggerFactory loggers;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DiscordModule? module;
    private string? error;

    public DiscordPlugin(IPluginHost host)
    { this.host = host; loggers = host.Loggers; }

    public string Id => "discord";
    public string Name => "Discord";
    public string Description => "Reads and replies to messages in Discord servers and DMs, and joins voice channels. Adds five tools so Ari can DM people, report back, and move between voice channels.";
    public string Author => "first-party";

    public byte[]? Icon => File.Exists(IconPath) ? File.ReadAllBytes(IconPath) : null;
    private string IconPath => Path.Combine(host.PluginsFolder, Id, "icon.png");

    public IReadOnlyDictionary<string, string> Source { get; } = PluginSource.Embedded(typeof(DiscordPlugin).Assembly);

    public string ToolsDescription => "Discord: DM anyone (and wait for their reply), close a DM conversation, find which voice channel a user is in, join a voice channel, or leave one.";

    public IReadOnlyList<ProvidedTool> Tools { get; } =
    [
        new("discord_dm_user", "Send a Discord DM to anyone by user ID. It opens a conversation with them that gets all their DMs, which you handle there and which reports back to you.", t => new DiscordDmUser(t)),
        new("discord_close_dm", "Close the DM conversation you opened with discord_dm_user once it's served its purpose.", _ => new DiscordCloseDm()),
        new("discord_list_voice_channels", "List all Discord voice channels a given user is currently in, across all servers Ari is a member of.", _ => new DiscordListVoiceChannels()),
        new("discord_join_voice_channel", "Join a Discord voice channel by its channel ID, leaving any voice channel she is already in within the same server.", _ => new DiscordJoinVoiceChannel()),
        new("discord_leave_voice_channel", "Leave the voice channel Ari is currently in, optionally only in one server.", _ => new DiscordLeaveVoiceChannel()),
    ];

    public IReadOnlyList<PluginSetting> Settings { get; } =
    [
        new("token", "Bot token", PluginSettingType.Secret, "From the Discord developer portal. Never shown again once saved.", "Paste a bot token"),
        new("masterDiscordId", "Master's Discord ID", PluginSettingType.Text, "Who controls Ari on Discord. Only the Master can change this.", "e.g. 123456789012345678"),
        new("whitelistedUserIds", "Whitelisted users", PluginSettingType.IdList, "People Ari will respond to, whether or not they have an account here.", "User ID"),
        new("watchedChannelIds", "Watched channels", PluginSettingType.IdList, "Channels she reads and can reply in.", "Channel ID"),
        new("allowedGuildIds", "Allowed servers", PluginSettingType.IdList, "Servers she is permitted to operate in.", "Server ID"),
    ];

    public IReadOnlyList<PluginAction> Actions { get; } =
    [
        new("deleteRecent", "Delete recent messages", "Deletes every message Ari sent in the last 24 hours, in all reachable channels and the Master's DM. This can't be undone.", Confirm: true),
    ];

    /// <summary>The running connection, or null. For the host to say goodbye on shutdown.</summary>
    public DiscordModule? Module => module;

    public bool Enabled => DiscordStore.Get().Enabled;

    public PluginState State => module is not null ? PluginState.Running : error is not null ? PluginState.Error : PluginState.Stopped;

    public string? Status =>
        module is not null ? module.ConnectionSummary
        : error is not null ? error
        : !Enabled ? "Switched off"
        : string.IsNullOrWhiteSpace(DiscordStore.Get().Token) ? "Needs a bot token" : "Not running";

    public IReadOnlyDictionary<string, object?> GetValues()
    {
        DiscordSettings s = DiscordStore.Get();
        return new Dictionary<string, object?>
        {
            ["token"] = !string.IsNullOrWhiteSpace(s.Token),
            ["masterDiscordId"] = s.MasterDiscordId == 0 ? "" : s.MasterDiscordId.ToString(),
            ["whitelistedUserIds"] = s.WhitelistedUserIds.Select(x => x.ToString()).ToArray(),
            ["watchedChannelIds"] = s.WatchedChannelIds.Select(x => x.ToString()).ToArray(),
            ["allowedGuildIds"] = s.AllowedGuildIds.Select(x => x.ToString()).ToArray(),
        };
    }

    public string? SetValues(IReadOnlyDictionary<string, JsonElement> changes, bool callerIsMaster)
    {
        DiscordSettings current = DiscordStore.Get();
        bool enabled = current.Enabled;
        string token = current.Token;
        ulong master = current.MasterDiscordId;
        List<ulong> users = current.WhitelistedUserIds, channels = current.WatchedChannelIds, guilds = current.AllowedGuildIds;

        foreach ((string key, JsonElement value) in changes)
        {
            switch (key)
            {
                case "enabled": enabled = value.ValueKind == JsonValueKind.True; break;
                case "token": if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) token = value.GetString()!.Trim(); break;
                case "masterDiscordId":
                {
                    string raw = value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
                    ulong parsed = 0;
                    if (raw.Length > 0 && !ulong.TryParse(raw, out parsed)) return "Master's Discord ID must be numeric.";
                    // Who the Master is on Discord decides who controls Ari there, so only the Master may change it.
                    if (parsed != current.MasterDiscordId && !(current.MasterDiscordId == 0 && raw.Length == 0) && !callerIsMaster)
                        return "Only the Master can change the Master's Discord ID.";
                    master = parsed;
                    break;
                }
                case "whitelistedUserIds": if (!TryIds(value, out users)) return "Whitelisted user IDs must be numeric."; break;
                case "watchedChannelIds": if (!TryIds(value, out channels)) return "Watched channel IDs must be numeric."; break;
                case "allowedGuildIds": if (!TryIds(value, out guilds)) return "Server IDs must be numeric."; break;
                default: return $"Discord has no setting called {key}.";
            }
        }

        DiscordStore.Set(new DiscordSettings
        {
            Enabled = enabled, Token = token, MasterDiscordId = master,
            WhitelistedUserIds = users, WatchedChannelIds = channels, AllowedGuildIds = guilds,
        });
        return null;
    }

    private static bool TryIds(JsonElement value, out List<ulong> ids)
    {
        ids = [];
        if (value.ValueKind != JsonValueKind.Array) return false;
        foreach (JsonElement item in value.EnumerateArray())
        {
            string s = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (!ulong.TryParse(s.Trim(), out ulong id)) return false;
            if (!ids.Contains(id)) ids.Add(id);
        }
        return true;
    }

    public async Task<string?> StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (module is not null) return null;
            error = null;
            DiscordSettings s = DiscordStore.Get();
            if (!s.Enabled) return null;
            if (string.IsNullOrWhiteSpace(s.Token)) { loggers.CreateLogger("ARI.Discord").LogWarning("Discord is enabled but no bot token is saved: add one on the Plugins page. Skipping Discord."); return "Needs a bot token"; }
            if (host.Llm is not { } model) return error = "Needs the language model module, which isn't running";

            loggers.CreateLogger("ARI.Discord").LogInformation("Discord plugin is enabled. Starting...");
            try
            {
                DiscordModule started = new(loggers, model, new DiscordConfig
                {
                    Enabled = s.Enabled, Token = s.Token, MasterDiscordId = s.MasterDiscordId,
                    WhitelistedUserIds = s.WhitelistedUserIds, WatchedChannelIds = s.WatchedChannelIds, AllowedGuildIds = s.AllowedGuildIds,
                });
                await started.StartAsync(host.Stopping);
                if (started.ExecuteTask is not null) host.Track(started.ExecuteTask);
                module = started;
                ARI.Common.Modules.Register(discord: started);
                return null;
            }
            catch (Exception ex) { return error = ex.Message; }
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync();
        try
        {
            error = null;
            if (module is not { } running) return;
            module = null;
            await running.NotifyOffline();
            await running.StopAsync(host.Stopping);
            running.Dispose();
            ARI.Common.Modules.ClearDiscord();
            loggers.CreateLogger("ARI.Discord").LogInformation("Discord stopped.");
        }
        finally { gate.Release(); }
    }

    public async Task<string> RunActionAsync(string key)
    {
        if (key != "deleteRecent") return $"Discord has no action called {key}.";
        if (module is null) return "Discord isn't running.";
        int count = await module.DeleteRecentMessagesAsync(TimeSpan.FromHours(24));
        return $"Deleted {count} message{(count == 1 ? "" : "s")}.";
    }
}
