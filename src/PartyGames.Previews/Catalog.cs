using System;
using PartyGames.Protocol;

namespace PartyGames.Previews;

/// <summary>
/// The server catalogue as the preview sees it: the same ids, names, blurbs and player ranges the live server
/// reports from <c>GameRegistry</c>, so a rendered screen matches what a player would actually get.
/// </summary>
internal static class Catalog
{
    public static readonly GameDescriptor[] Games =
    [
        new() { Id = "undercover", Name = "谁是卧底", Blurb = "多数人拿到同一个词,少数人拿到近义词.用一句话描述你的词,别被识破.", MinPlayers = 3, MaxPlayers = 20, SupportsScoring = true },
        new() { Id = "werewolf", Name = "狼人杀", Blurb = "狼人夜里杀人,白天全体投票放逐.找出所有狼人,或者让狼人活到人数占优.", MinPlayers = 6, MaxPlayers = 20, SupportsScoring = true },
        new() { Id = "roulette", Name = "俄罗斯轮盘", Blurb = "轮流扣扳机,六格弹巢里装 1-5 发.运气差的人先出局,活到最后的赢.", MinPlayers = 3, MaxPlayers = 20, SupportsScoring = true },
        new() { Id = "snowball", Name = "堆雪球", Blurb = "轮流滚自己的雪球:滚雪球 +2、加固 +1、砸人扣对方 3.雪球到 12 会崩,归零出局.", MinPlayers = 3, MaxPlayers = 20, SupportsScoring = true },
        new() { Id = "wordchain", Name = "词语接龙", Blurb = "轮流说词,下一个词要接住上一个词的最后一个字.接不上或超时就出局.", MinPlayers = 3, MaxPlayers = 20, SupportsScoring = true },
        new() { Id = "turtlesoup", Name = "海龟汤", Blurb = "主持人知道汤底,其他人靠「是/否」提问推理.猜对的人赢.", MinPlayers = 2, MaxPlayers = 12, SupportsScoring = true },
        new() { Id = "doudizhu", Name = "斗地主", Blurb = "一副牌三个人,叫了地主就得一对二.先出完手牌的一方赢,炸弹和王炸都翻倍.", MinPlayers = 3, MaxPlayers = 3, SupportsScoring = true },
        new() { Id = "xiangqi", Name = "中国象棋", Blurb = "九列十行,車馬炮兵各按规矩走.将死对方为胜,被将死为负.", MinPlayers = 2, MaxPlayers = 2, SupportsScoring = true },
    ];

    public static readonly StatRow[] MyStats =
    [
        new() { Game = "狼人杀", Wins = 3, Losses = 2 },
        new() { Game = "俄罗斯轮盘", Wins = 1, Losses = 4 },
        new() { Game = "斗地主", Wins = 4, Losses = 2 },
    ];
}
