using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MoreHeroes
{
    // Runs played with this mod do not go to the Steam leaderboards: they would not be comparable with vanilla runs.
    // FinishRunManager posts scores only through these four SteamManager wrappers (async void, so skipping them is safe;
    // the Task-returning *Leaderboard methods they await are left alone). Other mods may skip them too; that is harmless.
    internal static class NoLeaderboard
    {
        [HarmonyPatch]
        internal static class Patch
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(SteamManager), nameof(SteamManager.SetScore));
                yield return AccessTools.Method(typeof(SteamManager), nameof(SteamManager.SetObeliskScore));
                yield return AccessTools.Method(typeof(SteamManager), nameof(SteamManager.SetSingularityScore));
                yield return AccessTools.Method(typeof(SteamManager), nameof(SteamManager.SetWeeklyScore));
            }

            private static bool Prefix(MethodBase __originalMethod, int score)
            {
                Plugin.Log.LogInfo($"Steam leaderboard score not posted ({__originalMethod.Name}, {score}): runs with {Plugin.PluginName} stay off the leaderboards.");
                return false;
            }
        }
    }
}
