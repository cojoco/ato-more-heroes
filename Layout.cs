using System;
using System.Text;
using BattleMatch;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Combat layout for more than 4 heroes. Vanilla: hero x = -2.4 - position * 1.9 (positions 0..3 span -2.4 .. -8.1).
    // With N > 4 heroes the spacing is compressed so position N-1 still lands near the vanilla back slot, and the hero
    // GameObjects are scaled down a little to limit overlap. Hit boxes, health bars, pets and target arrows are children of
    // (or aimed at) the hero GameObject, so they follow its position and scale.
    internal static class Layout
    {
        private const float VanillaFront = 2.4f;
        private const float VanillaSpacing = 1.9f;
        private const float ExpandedFront = 2.0f;

        private static readonly AccessTools.FieldRef<CharacterItem, Hero> ItemHero = AccessTools.FieldRefAccess<CharacterItem, Hero>("_hero");

        internal static float BackLimit()
        {
            // Half the visible width in world units (Globals.sizeW is set from the main camera), minus room for the sprite:
            // measured in game, a hero body reaches about 1.3 units left of its position, so keep 1.5 for a small margin.
            float halfWidth = 9.6f;
            try
            {
                if (Globals.Instance != null && Globals.Instance.sizeW > 1f)
                {
                    halfWidth = Globals.Instance.sizeW * 0.5f;
                }
            }
            catch (Exception)
            {
            }
            float vanillaBack = VanillaFront + 3 * VanillaSpacing; // 8.1
            return Mathf.Clamp(halfWidth - 1.5f, vanillaBack - 0.6f, vanillaBack + 0.5f);
        }

        internal static float Spacing(int count)
        {
            if (count <= HeroSlots.VanillaCount)
            {
                return VanillaSpacing;
            }
            float configured = Plugin.HeroSpacing.Value;
            if (configured > 0.05f)
            {
                return configured;
            }
            return Mathf.Min(VanillaSpacing, (BackLimit() - ExpandedFront) / (count - 1));
        }

        internal static float Scale(int count)
        {
            if (count <= HeroSlots.VanillaCount)
            {
                return 1f;
            }
            float configured = Plugin.HeroScale.Value;
            if (configured > 0.05f)
            {
                return configured;
            }
            return Mathf.Max(0.7f, 1f - 0.05f * (count - HeroSlots.VanillaCount)); // 0.8 at 8 heroes
        }

        [HarmonyPatch(typeof(CharacterItem), nameof(CharacterItem.CalculatePositionX))]
        internal static class CalculatePositionXPatch
        {
            private static void Postfix(CharacterItem __instance, int _position, ref float __result)
            {
                int count = HeroSlots.Count;
                if (count <= HeroSlots.VanillaCount)
                {
                    return;
                }
                Hero hero = ItemHero(__instance);
                if (hero == null)
                {
                    return;
                }
                int pos = _position == -10 ? hero.Position : _position;
                __result = -ExpandedFront - pos * Spacing(count);
            }
        }

        // Stagger: with more than 4 heroes, even positions move up and odd positions move down so neighbours overlap less.
        internal static float StaggerY(int pos)
        {
            float s = Plugin.HeroStagger.Value;
            return pos % 2 == 0 ? s : -s;
        }

        // CalculateLocalPosition is the target used when placing a character (SetPosition) and for illusions, so y set here sticks.
        [HarmonyPatch(typeof(CharacterItem), nameof(CharacterItem.CalculateLocalPosition))]
        internal static class CalculateLocalPositionPatch
        {
            private static void Postfix(CharacterItem __instance, int position, ref Vector3 __result)
            {
                if (HeroSlots.Count <= HeroSlots.VanillaCount || Plugin.HeroStagger.Value <= 0f)
                {
                    return;
                }
                Hero hero = ItemHero(__instance);
                if (hero == null)
                {
                    return;
                }
                int pos = position == -10 ? hero.Position : position;
                __result.y = StaggerY(pos);
            }
        }

        // Vanilla resting draw order is -10000 + 1000 * (8 - Position * 2), so the front hero covers the one behind it.
        // With stagger, the lower row (nearer the camera) is drawn over the whole upper row. Drawing the upper row on top hid
        // the lower heroes' bodies, and did not help the upper heroes' HP bars and curse icons anyway: those are separate
        // sprites layered by the prefab, so only vertical distance keeps them clear. Lower heroes (odd p) get order p - 1,
        // upper heroes (even p) get 8 + p, which keeps the vanilla 0..14 range and even spacing (pets use order +-1).
        // Lower order = drawn on top. Only the standard resting call (goToFront false, order == Position * 2) is changed.
        [HarmonyPatch(typeof(CharacterItem), nameof(CharacterItem.DrawOrderSprites))]
        internal static class DrawOrderPatch
        {
            private static void Prefix(CharacterItem __instance, bool goToFront, ref int _order)
            {
                if (goToFront || HeroSlots.Count <= HeroSlots.VanillaCount || Plugin.HeroStagger.Value <= 0f)
                {
                    return;
                }
                Hero hero = ItemHero(__instance);
                if (hero == null || hero.HeroItem != __instance || _order != hero.Position * 2)
                {
                    return;
                }
                int p = hero.Position;
                _order = p % 2 == 0 ? 8 + p : p - 1;
            }
        }

        // Marker so a hero GameObject is only scaled once.
        internal class ScaledMarker : MonoBehaviour
        {
        }

        [HarmonyPatch(typeof(HeroItem), nameof(HeroItem.Init))]
        internal static class HeroItemInitPatch
        {
            private static void Postfix(HeroItem __instance)
            {
                try
                {
                    int count = HeroSlots.Count;
                    if (count <= HeroSlots.VanillaCount || MatchManager.Instance == null || __instance.GetComponent<ScaledMarker>() != null)
                    {
                        return;
                    }
                    float s = Scale(count);
                    if (Math.Abs(s - 1f) < 0.001f)
                    {
                        return;
                    }
                    __instance.gameObject.AddComponent<ScaledMarker>();
                    Transform t = __instance.transform;
                    t.localScale = new Vector3(t.localScale.x * s, t.localScale.y * s, t.localScale.z);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Scaling hero failed: {e}");
                }
            }
        }

        // Combat start summary.
        [HarmonyPatch(typeof(BattleTeamManager), nameof(BattleTeamManager.GenerateHeroes))]
        internal static class GenerateHeroesLog
        {
            private static void Postfix(BattleTeamManager __instance)
            {
                try
                {
                    Team heroes = __instance.GetTeam(TeamType.Hero);
                    int count = HeroSlots.Count;
                    var sb = new StringBuilder();
                    sb.Append($"Combat start: hero slots {count}, hero team list size {heroes?.Count ?? -1}, team size {AtOManager.Instance?.team?.Size ?? -1}");
                    if (count > HeroSlots.VanillaCount)
                    {
                        sb.Append($", spacing {Spacing(count):0.00}, scale {Scale(count):0.00}, stagger {Plugin.HeroStagger.Value:0.00}, back limit {BackLimit():0.00}");
                    }
                    if (heroes != null)
                    {
                        for (int i = 0; i < heroes.Count; i++)
                        {
                            Character c = heroes[i];
                            if (c == null)
                            {
                                sb.Append($"\n  slot {i}: empty");
                                continue;
                            }
                            string x = c.HeroItem != null ? c.HeroItem.transform.localPosition.x.ToString("0.00") : "?";
                            string y = c.HeroItem != null ? c.HeroItem.transform.localPosition.y.ToString("0.00") : "?";
                            sb.Append($"\n  slot {i}: {c.SourceName} ({c.SubclassName}) pos {c.Position} x {x} y {y} HP {c.HpCurrent}/{c.Hp} owner '{c.Owner}'");
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Combat start log failed: {e}");
                }
            }
        }
    }
}
