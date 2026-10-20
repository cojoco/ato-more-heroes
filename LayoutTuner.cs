using System;
using System.Text;
using BattleMatch;
using UnityEngine;

namespace MoreHeroes
{
    // Live tuning of the combat layout: hotkeys change HeroSpacing / HeroStagger (BepInEx saves the config on change) and
    // re-place every hero at once. Each change logs the new values and where the first two heroes' parts end up on screen,
    // so the defaults can be set from measurements instead of guesses.
    internal class LayoutTuner : MonoBehaviour
    {
        private const float SpacingStep = 0.1f;
        private const float StaggerStep = 0.1f;

        private void Update()
        {
            try
            {
                if (MatchManager.Instance == null || HeroSlots.Count <= HeroSlots.VanillaCount)
                {
                    return;
                }
                if (Plugin.SpacingDownKey.Value.IsDown())
                {
                    ChangeSpacing(-SpacingStep);
                }
                else if (Plugin.SpacingUpKey.Value.IsDown())
                {
                    ChangeSpacing(SpacingStep);
                }
                else if (Plugin.StaggerDownKey.Value.IsDown())
                {
                    ChangeStagger(-StaggerStep);
                }
                else if (Plugin.StaggerUpKey.Value.IsDown())
                {
                    ChangeStagger(StaggerStep);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Layout hotkey failed: {e}");
            }
        }

        private static void ChangeSpacing(float delta)
        {
            // 0 means automatic, so start from the value currently in use.
            float current = Layout.Spacing(HeroSlots.Count);
            Plugin.HeroSpacing.Value = Mathf.Clamp((float)Math.Round(current + delta, 2), 0.3f, 1.9f);
            Relayout();
        }

        private static void ChangeStagger(float delta)
        {
            Plugin.HeroStagger.Value = Mathf.Clamp((float)Math.Round(Plugin.HeroStagger.Value + delta, 2), 0f, 3f);
            Relayout();
        }

        internal static void Relayout()
        {
            Team team = MatchManager.Instance.GetTeamHero();
            if (team == null)
            {
                return;
            }
            foreach (Character c in team)
            {
                // SetPosition(instant) skips the move when x is unchanged, so set the root position directly.
                if (c is Hero hero && hero.Alive && hero.HeroItem != null)
                {
                    hero.HeroItem.transform.localPosition = hero.HeroItem.CalculateLocalPosition(hero.Position);
                    hero.HeroItem.DrawOrderSprites(false, hero.Position * 2);
                }
            }
            Plugin.Log.LogInfo($"Layout: spacing {Layout.Spacing(HeroSlots.Count):0.00}, stagger {Plugin.HeroStagger.Value:0.00}" + Measure(team));
        }

        // World-space vertical extent of each part of the first two heroes (one upper, one lower).
        private static string Measure(Team team)
        {
            var sb = new StringBuilder();
            Camera cam = Camera.main;
            if (cam != null && cam.orthographic)
            {
                float y = cam.transform.position.y;
                sb.Append($"\n  screen y {y - cam.orthographicSize:0.00} .. {y + cam.orthographicSize:0.00}");
            }
            for (int i = 0; i < team.Count && i < 2; i++)
            {
                if (!(team[i] is Hero hero) || hero.HeroItem == null)
                {
                    continue;
                }
                CharacterItem item = hero.HeroItem;
                sb.Append($"\n  {hero.SourceName} pos {hero.Position} at ({item.transform.position.x:0.00}, {item.transform.position.y:0.00}):");
                Part(sb, "body", item.CharImageT);
                Part(sb, "hpbar", item.healthBar);
                Part(sb, "buffs", item.GO_Buffs != null ? item.GO_Buffs.transform : null);
                Part(sb, "block", item.hpBlockT);
                Part(sb, "shield", item.hpShieldT);
            }
            return sb.ToString();
        }

        private static void Part(StringBuilder sb, string name, Transform t)
        {
            if (t == null)
            {
                return;
            }
            bool any = false;
            Bounds b = default;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!any)
                {
                    b = r.bounds;
                    any = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
            }
            if (any)
            {
                sb.Append($" {name} y {b.min.y:0.00}..{b.max.y:0.00} x {b.min.x:0.00}..{b.max.x:0.00};");
            }
            else
            {
                sb.Append($" {name} (hidden);");
            }
        }
    }
}
