using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace MoreHeroes
{
    // Creates heroes 5..N of a new run. Since 0.2.0 the player picks them on the hero selection screen (HeroSelectUi adds the
    // boxes, and HeroSelectionManager.BeginAdventure passes an N-entry array to AtOManager.SetTeamFromSubClasses). That method only
    // has room for 4 (GameTeam starts with 4 slots and SetHero does not grow it), so the Prefix below hands it the first 4 and the
    // Postfix creates the rest with the game's own AtOManager.CreateHeroBySubClass and appends them. They then go through the same
    // AtOManager.BeginAdventure steps as the first 4 (traits, starting item, level-0 traits, owner = local player in InitGame).
    // Fallback: if fewer than HeroCount heroes were picked (the boxes could not be added, not enough heroes unlocked), the team is
    // topped up from [Spike] ExtraHeroes as in 0.1.x, with a warning.
    internal static class ExtraHeroes
    {
        // Every playable subclass id the game creates in GameManager.GenerateHeroes, in that order. Fallback pool.
        internal static readonly string[] AllHeroIds =
        {
            "mercenary", "sentinel", "berserker", "warden", "cleric", "priest", "voodoowitch", "prophet", "elementalist", "pyromancer",
            "warlock", "loremaster", "ranger", "assassin", "archer", "minstrel", "bandit", "fallen", "paladin", "queen", "engineer",
            "valkyrie", "alchemist", "deathknight", "bloodmage", "exile"
        };

        private static readonly MethodInfo CreateHeroBySubClass = AccessTools.Method(typeof(AtOManager), "CreateHeroBySubClass");

        // True only while HeroSelectionManager.BeginAdventure runs, so SetTeamFromSubClasses calls from elsewhere
        // (e.g. AtOManager.TravelBetweenZones, which swaps the team in some zones) are left alone.
        private static bool inHeroSelectionBegin;

        [HarmonyPatch(typeof(HeroSelectionManager), nameof(HeroSelectionManager.BeginAdventure))]
        internal static class BeginAdventureScope
        {
            private static void Prefix()
            {
                inHeroSelectionBegin = true;
            }

            private static void Finalizer()
            {
                inHeroSelectionBegin = false;
            }
        }

        // Heroes picked for slots 5..N on the hero selection screen, taken off the array in the Prefix.
        private static List<string> pickedExtras;

        [HarmonyPatch(typeof(AtOManager), nameof(AtOManager.SetTeamFromSubClasses))]
        internal static class SetTeamPatch
        {
            private static void Prefix(ref string[] subClasses)
            {
                pickedExtras = null;
                if (!inHeroSelectionBegin || subClasses == null)
                {
                    return;
                }
                try
                {
                    var sb = new System.Text.StringBuilder("Run begins, picked heroes per slot:");
                    for (int i = 0; i < subClasses.Length; i++)
                    {
                        sb.Append($" {i}={(string.IsNullOrEmpty(subClasses[i]) ? "-" : subClasses[i])}");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    if (subClasses.Length <= HeroSlots.VanillaCount)
                    {
                        return;
                    }
                    pickedExtras = subClasses.Skip(HeroSlots.VanillaCount).Where(x => !string.IsNullOrEmpty(x)).Select(Norm).ToList();
                    subClasses = subClasses.Take(HeroSlots.VanillaCount).ToArray();
                    if (pickedExtras.Count > 0 && !GameManager.Instance.IsWeeklyChallenge() && !GameManager.Instance.IsMultiplayer())
                    {
                        Plugin.LastExtraHeroes.Value = string.Join(",", pickedExtras); // pre-fills the extra boxes next time
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Splitting the picked heroes failed: {e}");
                }
            }

            private static void Postfix(AtOManager __instance, string[] subClasses)
            {
                if (!inHeroSelectionBegin)
                {
                    return;
                }
                try
                {
                    AddExtraHeroes(__instance, subClasses, pickedExtras ?? new List<string>());
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Adding extra heroes failed, the run continues with the heroes created so far: {e}");
                }
                finally
                {
                    pickedExtras = null;
                }
            }
        }

        private static string SkipReason(AtOManager ato)
        {
            GameManager gm = GameManager.Instance;
            // In co-op the host's room decides (MorePlayers), so every player builds the same team whatever their own config says.
            if (!Plugin.Enabled.Value && !gm.IsMultiplayer())
            {
                return "[General] Enabled = false";
            }
            if (gm.IsMultiplayer() && !MultiplayerApi.Expanded)
            {
                return "multiplayer room with 4 heroes (more need the MorePlayers mod on every player's game)";
            }
            if (!gm.IsGameAdventure())
            {
                return $"only Adventure mode is supported in this spike (game type {gm.GameType})";
            }
            if (gm.IsTutorialGame() || ato.IsFirstGame())
            {
                return "tutorial run";
            }
            if (ato.IsCombatTool)
            {
                return "combat tool";
            }
            if (TargetCount() <= HeroSlots.VanillaCount)
            {
                return "[General] HeroCount = 4";
            }
            return null;
        }

        // Heroes the run should have: HeroCount in single player, the room's count in co-op (MorePlayers).
        private static int TargetCount()
        {
            return GameManager.Instance.IsMultiplayer() ? MultiplayerApi.Current() : Plugin.TargetHeroCount;
        }

        private static void AddExtraHeroes(AtOManager ato, string[] picks, List<string> picked)
        {
            string reason = SkipReason(ato);
            if (reason != null)
            {
                if (GameManager.Instance.IsMultiplayer() || picked.Count > 0)
                {
                    Plugin.Log.LogWarning($"No extra heroes added: {reason}." + (picked.Count > 0 ? $" Ignoring picks for slots 5+: {string.Join(", ", picked)}." : ""));
                }
                else
                {
                    Plugin.Log.LogInfo($"No extra heroes added: {reason}.");
                }
                return;
            }

            GameTeam team = ato.team;
            for (int i = 0; i < team.Size; i++)
            {
                if (!team.HasHero(i) || team.GetHero(i).HeroData == null)
                {
                    Plugin.Log.LogWarning($"No extra heroes added: team slot {i} is empty (sandbox with fewer heroes?).");
                    return;
                }
            }

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in (picks ?? new string[0]).Take(HeroSlots.VanillaCount))
            {
                if (!string.IsNullOrEmpty(p))
                {
                    taken.Add(Norm(p));
                }
            }
            foreach (Hero h in team)
            {
                if (h?.SubclassName != null)
                {
                    taken.Add(Norm(h.SubclassName));
                }
            }

            // Heroes picked in the extra boxes, in box order.
            var fromBoxes = new List<string>();
            foreach (string id in picked)
            {
                if (team.Size >= HeroSlots.MaxCount)
                {
                    break;
                }
                if (taken.Contains(id))
                {
                    Plugin.Log.LogWarning($"Picked hero '{id}' is already in the team, skipped (hero picks must be distinct).");
                    continue;
                }
                Hero hero = CreateHero(ato, id);
                if (hero?.HeroData == null)
                {
                    Plugin.Log.LogWarning($"Could not create picked hero '{id}'.");
                    continue;
                }
                int slot = team.Size;
                team.heroes.Add(null);
                team.SetHero(hero, slot);
                taken.Add(id);
                fromBoxes.Add($"{id} (slot {slot})");
            }
            if (picked.Count > 0)
            {
                Plugin.Log.LogInfo($"Picked extra heroes created: {(fromBoxes.Count == 0 ? "none" : string.Join(", ", fromBoxes))}.");
            }

            int needed = TargetCount() - team.Size;
            if (needed <= 0)
            {
                LogTeam(team);
                return;
            }
            if (GameManager.Instance.IsMultiplayer())
            {
                // Every co-op hero belongs to a player who picked it; a hero from the host's config would have no owner.
                Plugin.Log.LogWarning($"Co-op: only {team.Size} of {TargetCount()} heroes were picked; no fallback heroes in co-op.");
                LogTeam(team);
                return;
            }
            Plugin.Log.LogWarning($"Only {team.Size} heroes were picked but HeroCount is {Plugin.TargetHeroCount}; topping up {needed} from " +
                                  "[Spike] ExtraHeroes (fallback: the hero selection screen should have a box per hero, see the 'Hero selection' log lines).");
            var candidates = new List<(string Id, bool FromConfig)>();
            foreach (string id in (Plugin.ExtraHeroes.Value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add((Norm(id), true));
            }
            foreach (string id in AllHeroIds)
            {
                candidates.Add((id, false));
            }

            var added = new List<string>();
            foreach ((string id, bool fromConfig) in candidates)
            {
                if (added.Count >= needed)
                {
                    break;
                }
                if (taken.Contains(id))
                {
                    if (fromConfig)
                    {
                        Plugin.Log.LogInfo($"ExtraHeroes: '{id}' skipped, already in the team (hero picks must be distinct).");
                    }
                    continue;
                }
                string why = Unusable(id, requireUnlocked: !fromConfig);
                if (why != null)
                {
                    if (fromConfig)
                    {
                        Plugin.Log.LogWarning($"ExtraHeroes: '{id}' skipped: {why}.");
                    }
                    continue;
                }
                Hero hero = CreateHero(ato, id);
                if (hero?.HeroData == null)
                {
                    Plugin.Log.LogWarning($"ExtraHeroes: could not create '{id}', trying the next one.");
                    taken.Add(id);
                    continue;
                }
                int slot = team.Size;
                team.heroes.Add(null);
                team.SetHero(hero, slot);
                taken.Add(id);
                added.Add($"{id} (slot {slot}{(fromConfig ? "" : ", fallback")})");
            }

            Plugin.Log.LogInfo($"Fallback extra heroes added: {(added.Count == 0 ? "none" : string.Join(", ", added))}. Team size is now {team.Size}.");
            if (added.Count < needed)
            {
                Plugin.Log.LogWarning($"Wanted {needed} extra heroes but only found {added.Count} usable ones.");
            }
            LogTeam(team);
        }

        private static void LogTeam(GameTeam team)
        {
            var parts = new List<string>();
            for (int i = 0; i < team.Size; i++)
            {
                Hero h = team.GetHero(i);
                parts.Add($"{i}={(h?.HeroData == null ? "-" : h.SubclassName)}");
            }
            Plugin.Log.LogInfo($"Run team ({team.Size} heroes): {string.Join(" ", parts)}");
        }

        private static string Norm(string id)
        {
            return id.Trim().Replace(" ", "").ToLowerInvariant();
        }

        private static string Unusable(string id, bool requireUnlocked)
        {
            SubClassData scd = Globals.Instance.GetSubClassData(id);
            if (scd == null)
            {
                return "no such hero subclass id";
            }
            try
            {
                if (!string.IsNullOrEmpty(scd.Sku) && !SteamManager.Instance.PlayerHaveDLC(scd.Sku))
                {
                    return $"needs DLC {scd.Sku}";
                }
            }
            catch (Exception)
            {
                // No Steam: treat as owned only for config entries.
                if (requireUnlocked)
                {
                    return "DLC ownership unknown";
                }
            }
            if (requireUnlocked && PlayerManager.Instance != null && !PlayerManager.Instance.IsHeroUnlocked(id))
            {
                return "not unlocked on this profile";
            }
            return null;
        }

        private static Hero CreateHero(AtOManager ato, string id)
        {
            // CreateHeroBySubClass reads these per-subclass entries; picked heroes have them, the extras may not.
            AtOManager.Instance.PerksManager.heroPerks.TryGetValue(id, out List<string> perks);
            if (perks == null)
            {
                // Co-op: perks come from the hero's owner (PerksManager.SetPlayerPerks); never fill them from the host's profile.
                perks = GameManager.Instance.IsMultiplayer() ? new List<string>()
                    : PlayerManager.Instance.GetHeroPerks(id, forceFromPlayerManager: true) ?? new List<string>();
                AtOManager.Instance.PerksManager.heroPerks[id] = perks;
            }
            HeroSelectionManager hsm = HeroSelectionManager.Instance;
            if (hsm != null)
            {
                if (hsm.playerHeroSkinsDict != null && !hsm.playerHeroSkinsDict.ContainsKey(id))
                {
                    hsm.playerHeroSkinsDict[id] = PlayerManager.Instance.GetActiveSkin(id);
                }
                if (hsm.playerHeroCardbackDict != null && !hsm.playerHeroCardbackDict.ContainsKey(id))
                {
                    hsm.playerHeroCardbackDict[id] = PlayerManager.Instance.GetActiveCardback(id);
                }
            }

            try
            {
                if (CreateHeroBySubClass != null)
                {
                    Hero hero = CreateHeroBySubClass.Invoke(ato, new object[] { id }) as Hero;
                    if (hero?.HeroData != null)
                    {
                        return hero;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"CreateHeroBySubClass('{id}') failed ({(e.InnerException ?? e).Message}); using the plain fallback.");
            }

            // Fallback: the bare essentials of CreateHeroBySubClass (no skin/cardback/perk rank).
            Hero fallback = GameManager.Instance.CreateHero(id);
            if (fallback?.HeroData == null)
            {
                return null;
            }
            fallback.ReassignInitialItem();
            fallback.PerkList = perks;
            if (fallback.Energy < 0)
            {
                fallback.Energy = 0;
            }
            if (fallback.EnergyCurrent < 0)
            {
                fallback.EnergyCurrent = 0;
            }
            return fallback;
        }
    }
}
