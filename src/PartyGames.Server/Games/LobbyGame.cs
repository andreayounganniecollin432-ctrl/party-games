using PartyGames.Protocol;

namespace PartyGames.Server.Games;

/// <summary>常驻大厅：没有对局，只提供聊天。</summary>
public sealed class LobbyGame : IGameModule
{
    public string Id => "lobby";
    public string DisplayName => "聊天大厅";
    public string Blurb => "没有对局，随便聊。所有人都在这里。";
    public int MinPlayers => 1;
    public int MaxPlayers => 200;

    public void Start(Room room)
    {
        room.Log("system", "欢迎来到聊天大厅。");
        room.SyncAll();
    }

    public void HandleAction(Room room, Player player, ActionRequest request) { }
    public void Tick(Room room, long nowMs) { }

    public GameView BuildView(Room room, Player viewer) => new()
    {
        Title = "聊天大厅",
        Subtitle = $"{room.Players.Count} 人在线",
        Instruction = "随便聊，这里不需要开游戏。",
        Players = room.Players.Select(p => new GamePlayerCard
        {
            Id = p.Id,
            Name = p.Name,
            IsYou = p.Id == viewer.Id,
            IsHost = p.Id == room.HostId,
            IsAlive = true,
            Sub = p.IsOnline ? null : "离线",
        }).ToList(),
    };

    public void OnPlayerDisconnected(Room room, Player player) => room.SyncAll();
    public void OnPlayerLeft(Room room, Player player) => room.SyncAll();
}