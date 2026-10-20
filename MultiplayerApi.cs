using System;

namespace MoreHeroes
{
    // The hook the MorePlayers mod uses to turn MoreHeroes on in co-op. MoreHeroes itself has no multiplayer code: without
    // MorePlayers (RoomHeroCount == null) a co-op game always has 4 heroes and every patch in this mod stays a no-op there.
    public static class MultiplayerApi
    {
        // Set by MorePlayers. Returns the hero count every player in the current co-op room agreed on (4..8); 4 when the room is
        // vanilla (a player without the mods, or the host picked 4).
        public static Func<int> RoomHeroCount;

        // The co-op hero count right now, 4 if MorePlayers is missing or its callback fails.
        public static int Current()
        {
            try
            {
                if (RoomHeroCount == null)
                {
                    return HeroSlots.VanillaCount;
                }
                int n = RoomHeroCount();
                return n < HeroSlots.VanillaCount ? HeroSlots.VanillaCount : n > HeroSlots.MaxCount ? HeroSlots.MaxCount : n;
            }
            catch (Exception)
            {
                return HeroSlots.VanillaCount;
            }
        }

        // True when the current co-op room plays with more than 4 heroes.
        public static bool Expanded => Current() > HeroSlots.VanillaCount;

        // [General] HeroCount (4..8), or 4 with [General] Enabled = false: what the host offers when creating a co-op room.
        public static int ConfiguredHeroCount => Plugin.Enabled.Value ? Plugin.TargetHeroCount : HeroSlots.VanillaCount;
    }
}
