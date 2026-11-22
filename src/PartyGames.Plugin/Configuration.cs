using System.Text.Json.Serialization;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PartyGames;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>音效开关</summary>
    /// <summary>把房间聊天同步到游戏聊天框（只有自己看得到）</summary>
    public bool SyncChatToGame { get; set; } = true;

    public bool SoundEnabled { get; set; } = true;

    /// <summary>语音播报（轮到谁）</summary>
    public bool VoiceEnabled { get; set; } = true;

    /// <summary>屏幕提示停留几秒（2-10），设置页可调。</summary>
    public int ToastSeconds { get; set; } = 4;

    /// <summary>自定义头像图片路径（本地文件），留空用默认色块</summary>
    public string AvatarPath { get; set; } = string.Empty;

    /// <summary>自定义背景图片路径（本地文件），留空用默认深色</summary>
    public string BackgroundPath { get; set; } = string.Empty;

    /// <summary>背景压暗程度 0-100</summary>
    public int BackgroundDim { get; set; } = 60;

    public string ServerUrl { get; set; } = "ws://111.230.152.180:5180/ws";

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Last room we were in, used to offer a one-click resume.</summary>
    public string LastRoomCode { get; set; } = string.Empty;

    /// <summary>Identity issued by the server, so a reconnect can reclaim the same seat.</summary>
    public string LastPlayerId { get; set; } = string.Empty;

    public bool OpenOnLogin { get; set; }

    /// <summary>液化玻璃背景；关掉就用自绘背景图（显卡/驱动不支持时用这个）。</summary>
    public bool GlassEnabled { get; set; } = true;

    public int PreferredMaxPlayers { get; set; } = 8;

    [JsonIgnore] private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
