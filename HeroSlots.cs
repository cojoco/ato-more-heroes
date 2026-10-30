using System;

namespace MoreHeroes
{
    // The single source of truth for "how many hero slots does the game have right now".
    // Every transpiled constant goes through Shift(), so with a 4-hero team (or in multiplayer) the game behaves exactly as vanilla.
    public static class HeroSlots
    {
        public const int VanillaCount = 4;
        public const int MaxCount = 8;

        // Modes that can have extra heroes: Adventure, Obelisk Challenge and Singularity. Not the weekly challenge, whose 4 heroes
        // are set by the week. The game's IsObeliskChallenge() is also true for weekly runs, so weekly is checked first.
        internal static bool ModeAllowsExtraHeroes(GameManager gm)
        {
            return gm != null && !gm.IsWeeklyChallenge() && (gm.IsGameAdventure() || gm.IsObeliskChallenge() || gm.IsSingularity());
        }

        // The Obelisk Challenge (not weekly) lets you pick heroes this profile has not unlocked (HeroSelectionManager unblocks
        // them); heroes from a DLC the player does not own stay blocked in every mode.
        internal static bool UnlockRequired(GameManager gm)
        {
            return gm == null || !gm.IsObeliskChallenge() || gm.IsWeeklyChallenge();
        }

        // Hero slot count: the size of AtOManager.team (clamped 4..8). In multiplayer always 4, unless the MorePlayers mod says the
        // room plays with more heroes (MultiplayerApi).
        // The team size is what matters, not the config, so an 8-hero save keeps working even with Enabled=false,
        // and a run where adding heroes failed falls back to vanilla behaviour.
        public static int Count
        {
            get
            {
                try
                {
                    GameManager gm = GameManager.Instance;
                    if (gm == null || (gm.IsMultiplayer() && !MultiplayerApi.Expanded))
                    {
                        return VanillaCount;
                    }
                    AtOManager ato = AtOManager.Instance;
                    GameTeam team = ato != null ? ato.team : null;
                    if (team == null)
                    {
                        return VanillaCount;
                    }
                    int n = team.Size;
                    if (n < VanillaCount)
                    {
                        return VanillaCount;
                    }
                    return n > MaxCount ? MaxCount : n;
                }
                catch (Exception)
                {
                    return VanillaCount;
                }
            }
        }

        public static bool Expanded => Count > VanillaCount;

        // Called from transpiled IL: the original constant k is kept on the stack and this turns it into k + (Count - 4).
        // So 4 -> Count (hero loop bound), 3 -> Count - 1 (last hero position), 8 -> Count + 4 (heroes + NPCs), and sync-code
        // array indices that sit after the hero block (8, 9, 11, 12) move by the number of extra heroes.
        public static int Shift(int k)
        {
            return k + Count - VanillaCount;
        }
    }
}
