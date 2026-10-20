using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Rewards screen with a row per hero. The scene has 4 CharacterReward rows (children of "Rewards", local y 2.6, 0.4, -1.8,
    // -4.0, x -0.2; the parent sits at y 0.15 with scale 0.95). With N > 4 heroes the missing rows are cloned from row 4 (still
    // inactive and untouched when Start runs) and all N rows are laid out in columns, scaled down so they fit the screen.
    // Everything in a row finds its hero through the index given to CharacterReward.Init (card names, deck button auxInt) or
    // through its own transform parents (dust button), so clones report the right hero.
    // The same code runs for RewardsManagerMultiple (town minigame rewards), which uses the same scene layout.
    internal static class RewardsUi
    {
        // Measured from the Rewards scene and a 2026-10-20 screenshot (row scale 0.57), in row-local units: the row's left edge
        // (border line, deck button at x -7, portrait) is at about -7.7; cards are children of CharacterReward.cardsTransform
        // ("Cards", vanilla scale 1) at -1.9 + 1.95 * i, about 1.35 wide and 2.02 tall, up to 4 (dual-class heroes get 4, others
        // 3); the dust option (ChooseDust and DustQ, RectTransforms anchored at x 6) is about 1.86 wide. Vanilla rows are 2.2 apart.
        // With more than 4 heroes the cards are scaled up inside the row (RewardCardScale) and the dust option follows the last
        // card actually dealt, so a row of 3 cards does not keep the empty 4th slot. The cards are also dealt closer together
        // (RewardCardSpacing, through a call inserted in CharacterReward.DoCard).
        private const float RowLeft = -7.7f;
        private const float CardsLeft = -2.575f;     // left edge of the first card in Cards-local units (centre -1.9)
        private const float VanillaCardPitch = 1.95f;
        private const float DefaultCardPitch = 1.6f; // 0.25 between cards instead of 0.6 (0.2.18, user: "slightly decreasing the space")
        private const float CardWidth = 1.35f;
        private const float CardHeight = 2.02f;
        private const float DustHalfWidth = 0.93f;
        private const float DustGap = 0.45f;         // vanilla gap between the 4th card and the dust option
        private const float VanillaDustX = 6f;
        private const float SelectedCardX = -4f;     // where ShowSelected/SelectMinigameCard put the picked card (Cards-local)
        private const float DefaultCardScale = 1.3f;
        private const float VanillaPitch = 2.2f;
        private const float VanillaTopY = 2.6f;
        private const float VanillaBottomY = -4.0f;
        private const float ScreenMargin = 0.35f; // world units left free at each column edge

        private static readonly AccessTools.FieldRef<RewardsManager, int> NumRewards =
            AccessTools.FieldRefAccess<RewardsManager, int>("numRewards");

        // Rows that exist beyond the vanilla 4 and have a hero to show. Used by the transpiled loops (ShowRewardsCo,
        // SetCardReward, ControllerMovement), so they never run past characterRewardArray or theTeam.
        internal static int Extra()
        {
            try
            {
                RewardsManager rm = RewardsManager.Instance;
                if (rm == null || rm.characterRewardArray == null)
                {
                    return 0;
                }
                int rows = rm.characterRewardArray.Length;
                if (rm.theTeam != null)
                {
                    rows = Math.Min(rows, rm.theTeam.Length);
                }
                return Math.Max(0, Math.Min(rows, HeroSlots.MaxCount) - HeroSlots.VanillaCount);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Called from transpiled IL: k (a 4) becomes k + extra rows.
        public static int Shift(int k)
        {
            return k + Extra();
        }

        [HarmonyPatch(typeof(RewardsManager), "Start")]
        internal static class StartPatch
        {
            // Before Start: Start runs SetRewards, which in single player goes straight into ShowRewardsCo.
            private static void Prefix(RewardsManager __instance)
            {
                Plugin.ReloadConfig();
                try
                {
                    AddRows(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Rewards: adding rows for heroes 5..N failed, extra heroes will get an automatic pick: {e}");
                }
            }

            // Start sets cardSelectedArr = new string[4]; give it one entry per row.
            private static void Postfix(RewardsManager __instance)
            {
                try
                {
                    int rows = __instance.characterRewardArray?.Length ?? 0;
                    if (rows > HeroSlots.VanillaCount && __instance.cardSelectedArr != null && __instance.cardSelectedArr.Length < rows)
                    {
                        string[] arr = __instance.cardSelectedArr;
                        Array.Resize(ref arr, rows);
                        __instance.cardSelectedArr = arr;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Rewards: growing cardSelectedArr failed: {e}");
                }
            }
        }

        private static void AddRows(RewardsManager rm)
        {
            int n = HeroSlots.Count;
            Transform[] rows = rm.characterRewardArray;
            if (n <= HeroSlots.VanillaCount || rows == null)
            {
                return;
            }
            if (rows.Length != HeroSlots.VanillaCount || Array.Exists(rows, r => r == null || r.GetComponent<CharacterReward>() == null))
            {
                Plugin.Log.LogError($"Rewards: expected 4 CharacterReward rows in the scene, found {rows.Length}; leaving the screen vanilla.");
                return;
            }
            Transform template = rows[HeroSlots.VanillaCount - 1];
            Transform parent = template.parent;
            var all = new List<Transform>(rows);
            for (int i = HeroSlots.VanillaCount; i < n; i++)
            {
                // Instantiate(original, parent) keeps the local position/scale; the clone stays inactive like the template.
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
                clone.name = $"CharacterReward ({i}) MoreHeroes";
                all.Add(clone.transform);
            }
            rm.characterRewardArray = all.ToArray();
            // The cards are not dealt yet: lay out for 4 so nothing can overflow, then again in ShowRewardsCo with the real count.
            Layout(rm, all, parent, HeroSlots.VanillaCount, false);
            rm.StartCoroutine(LogBoundsLater(rm));
        }

        // ShowRewardsCo shows the rows one by one; by the time it is called the cards of every hero are in cardsByOrder.
        [HarmonyPatch(typeof(RewardsManager), "ShowRewardsCo")]
        internal static class ShowRewardsPatch
        {
            private static void Prefix(RewardsManager __instance)
            {
                try
                {
                    Transform[] rows = __instance.characterRewardArray;
                    if (Extra() == 0 || rows == null || rows.Length == 0 || rows[0] == null)
                    {
                        return;
                    }
                    Layout(__instance, new List<Transform>(rows), rows[0].parent, MaxCards(__instance), true);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Rewards: laying out the rows for the dealt cards failed: {e}");
                }
            }
        }

        // Most cards any row will show (CharacterReward.Show deals at most 4, skipping null entries).
        private static int MaxCards(RewardsManager rm)
        {
            int max = 1;
            if (rm.cardsByOrder == null)
            {
                return HeroSlots.VanillaCount;
            }
            foreach (string[] cards in rm.cardsByOrder.Values)
            {
                int n = 0;
                for (int i = 0; cards != null && i < cards.Length && i < HeroSlots.VanillaCount; i++)
                {
                    if (!string.IsNullOrEmpty(cards[i]))
                    {
                        n++;
                    }
                }
                max = Math.Max(max, n);
            }
            return max;
        }

        // Distance between card centres in Cards-local units: vanilla with 4 heroes, otherwise RewardCardSpacing (0 = 1.6).
        private static float CardSpacing()
        {
            float v = Plugin.RewardCardSpacing.Value;
            return v > 0.05f ? Mathf.Clamp(v, CardWidth, 3f) : DefaultCardPitch;
        }

        // Called from CharacterReward.DoCard's IL: x = -1.9 + position * CardPitch(1.95). The first card stays where vanilla puts it.
        public static float CardPitch(float vanilla)
        {
            try
            {
                return Extra() == 0 ? vanilla : CardSpacing();
            }
            catch (Exception)
            {
                return vanilla;
            }
        }

        // IlPatches custom edit for CharacterReward.DoCard: insert "call CardPitch" after its single ldc.r4 1.95 (next: mul).
        internal static string EditCardPitch(List<CodeInstruction> list)
        {
            var hits = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float f && Mathf.Abs(f - VanillaCardPitch) < 0.0001f)
                {
                    hits.Add(i);
                }
            }
            if (hits.Count != 1)
            {
                return $"expected 1 x ldc.r4 1.95, found {hits.Count}";
            }
            int at = hits[0];
            if (at + 1 >= list.Count || list[at + 1].opcode != OpCodes.Mul)
            {
                return "ldc.r4 1.95 is not followed by mul";
            }
            list.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(RewardsUi), nameof(CardPitch))));
            return null;
        }

        // Row-local x of the dust option's centre and the row's right edge for this many cards at this card scale.
        private static float DustX(int cards, float cardScale)
        {
            return CardsLeft + cardScale * (CardSpacing() * (cards - 1) + CardWidth) + DustGap + DustHalfWidth;
        }

        private static float RowRight(int cards, float cardScale)
        {
            return DustX(cards, cardScale) + DustHalfWidth;
        }

        // Scale the cards about the first card's left edge and put the dust option after the last card. Absolute values, so
        // laying out twice gives the same result.
        private static void LayoutInside(Transform row, int cards, float cardScale)
        {
            CharacterReward cr = row.GetComponent<CharacterReward>();
            if (cr == null)
            {
                return;
            }
            if (cr.cardsTransform != null)
            {
                Vector3 p = cr.cardsTransform.localPosition;
                cr.cardsTransform.localPosition = new Vector3(CardsLeft * (1f - cardScale), p.y, p.z);
                cr.cardsTransform.localScale = new Vector3(cardScale, cardScale, 1f);
            }
            float dustX = DustX(cards, cardScale);
            foreach (Transform t in new[] { cr.chooseDust, cr.quantityDust })
            {
                if (t != null)
                {
                    t.localPosition = new Vector3(dustX, t.localPosition.y, t.localPosition.z);
                }
            }
        }

        // ShowSelected and SelectMinigameCard move the picked card to x -4 in Cards-local units. With the cards scaled up that
        // would reach further into the portrait than vanilla, so keep the card's left edge where vanilla has it.
        [HarmonyPatch]
        internal static class SelectedCardPatch
        {
            private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(CharacterReward), "ShowSelected");
                yield return AccessTools.Method(typeof(CharacterReward), "SelectMinigameCard");
            }

            private static void Postfix(CharacterReward __instance)
            {
                try
                {
                    Transform cards = __instance.cardsTransform;
                    if (cards == null)
                    {
                        return;
                    }
                    float k = cards.localScale.x;
                    if (Mathf.Abs(k - 1f) < 0.01f)
                    {
                        return;
                    }
                    float leftEdge = SelectedCardX - CardWidth * 0.5f;
                    float x = (leftEdge + CardWidth * 0.5f * k - cards.localPosition.x) / k;
                    foreach (Transform card in cards)
                    {
                        if (Mathf.Abs(card.localPosition.x - SelectedCardX) < 0.01f)
                        {
                            card.localPosition = new Vector3(x, card.localPosition.y, card.localPosition.z);
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Rewards: placing the picked card failed: {e.Message}");
                }
            }
        }

        private static Camera GameCamera()
        {
            Camera cam = null;
            try
            {
                cam = GameManager.Instance != null ? GameManager.Instance.cameraMain : null;
            }
            catch (Exception)
            {
            }
            return cam != null ? cam : Camera.main;
        }

        // Screen extent in world units: (centre x, centre y, half width, half height). Falls back to 16:9 at ortho size 5.4.
        internal static Vector4 ScreenWorld()
        {
            Camera cam = GameCamera();
            if (cam != null && cam.orthographic)
            {
                float hh = cam.orthographicSize;
                return new Vector4(cam.transform.position.x, cam.transform.position.y, hh * cam.aspect, hh);
            }
            return new Vector4(0f, 0f, 9.6f, 5.4f);
        }

        private static void Layout(RewardsManager rm, List<Transform> rows, Transform parent, int cards, bool log)
        {
            cards = Mathf.Clamp(cards, 1, HeroSlots.VanillaCount);
            float cardScale = Plugin.RewardCardScale.Value > 0.05f ? Plugin.RewardCardScale.Value : DefaultCardScale;
            float rowWidth = RowRight(cards, cardScale) - RowLeft;
            float rowCenterX = (RowRight(cards, cardScale) + RowLeft) * 0.5f;
            int n = rows.Count;
            int cols = Mathf.Clamp(Plugin.RewardColumns.Value, 1, 4);
            cols = Math.Min(cols, n);
            int perCol = (n + cols - 1) / cols;

            Vector4 screen = ScreenWorld();
            float parentScale = parent != null ? Mathf.Abs(parent.lossyScale.x) : 1f;
            if (parentScale < 0.01f)
            {
                parentScale = 1f;
            }
            Vector3 parentPos = parent != null ? parent.position : Vector3.zero;

            // Vertical: rows of a column are centred on the vanilla block (between the vanilla top and bottom rows).
            float pitch = Plugin.RewardRowSpacing.Value > 0.05f
                ? Plugin.RewardRowSpacing.Value
                : (perCol <= 4 ? VanillaPitch : (VanillaTopY - VanillaBottomY) / (perCol - 1));
            float centerY = (VanillaTopY + VanillaBottomY) * 0.5f + Plugin.RewardOffsetY.Value;
            float firstY = centerY + pitch * (perCol - 1) * 0.5f;

            // Horizontal: columns split the screen width evenly unless RewardColumnSpacing (world units) is set.
            float screenWLocal = 2f * screen.z / parentScale;
            float colSpacing = Plugin.RewardColumnSpacing.Value > 0.05f ? Plugin.RewardColumnSpacing.Value / parentScale : screenWLocal / cols;
            float screenCenterLocal = (screen.x - parentPos.x) / parentScale;

            float scale = Plugin.RewardRowScale.Value;
            string scaleNote = "config";
            if (scale <= 0.05f)
            {
                float widthFit = (colSpacing - 2f * ScreenMargin / parentScale) / rowWidth;
                float heightFit = pitch / VanillaPitch;
                // Scaled-up cards must not reach the next row's cards (0.15 apart at least).
                float cardFit = (pitch - 0.15f) / (CardHeight * cardScale);
                scale = Mathf.Clamp(Mathf.Min(1f, widthFit, heightFit, cardFit), 0.3f, 1f);
                scaleNote = $"auto: width fit {widthFit:0.00}, height fit {heightFit:0.00}, card fit {cardFit:0.00}";
            }

            var sb = new StringBuilder();
            sb.Append($"Rewards: {n} rows in {cols} column(s) of {perCol}, row scale {scale:0.00} ({scaleNote}), up to {cards} cards per row " +
                      $"at card scale {cardScale:0.00} (cards {cardScale * scale:0.00} of vanilla) {CardSpacing():0.00} apart, dust option at row x {DustX(cards, cardScale):0.00}, " +
                      $"row width {rowWidth:0.00}, row spacing {pitch:0.00}, " +
                      $"column spacing {colSpacing * parentScale:0.00} (world), offset y {Plugin.RewardOffsetY.Value:0.00}. " +
                      $"Screen x {screen.x - screen.z:0.00}..{screen.x + screen.z:0.00}, y {screen.y - screen.w:0.00}..{screen.y + screen.w:0.00}; " +
                      $"rows parent '{parent?.name}' at ({parentPos.x:0.00}, {parentPos.y:0.00}) scale {parentScale:0.00}.");
            for (int i = 0; i < n; i++)
            {
                int c = i / perCol;
                int r = i % perCol;
                float colCenter = screenCenterLocal + (c - (cols - 1) * 0.5f) * colSpacing;
                Transform t = rows[i];
                t.localPosition = new Vector3(colCenter - rowCenterX * scale, firstY - r * pitch, t.localPosition.z);
                t.localScale = new Vector3(scale, scale, 1f);
                LayoutInside(t, cards, cardScale);
                float halfW = rowWidth * 0.5f * scale * parentScale;
                Vector3 wp = t.position;
                float cx = wp.x + rowCenterX * scale * parentScale;
                sb.Append($"\n  row {i} ({t.name}): local ({t.localPosition.x:0.00}, {t.localPosition.y:0.00}) world ({wp.x:0.00}, {wp.y:0.00}), " +
                          $"expected x {cx - halfW:0.00}..{cx + halfW:0.00}, y {wp.y - 1.3f * scale * parentScale:0.00}..{wp.y + 1.3f * scale * parentScale:0.00}");
            }
            if (log)
            {
                Plugin.Log.LogInfo(sb.ToString());
            }
        }

        // A few seconds after the screen opens (rows and cards are dealt by then), log what each row really covers on screen.
        private static IEnumerator LogBoundsLater(RewardsManager rm)
        {
            yield return new WaitForSeconds(3f);
            try
            {
                if (rm == null || rm.characterRewardArray == null)
                {
                    yield break;
                }
                Vector4 screen = ScreenWorld();
                var sb = new StringBuilder();
                sb.Append($"Rewards layout check (renderer bounds, world units; screen x {screen.x - screen.z:0.00}..{screen.x + screen.z:0.00}, " +
                          $"y {screen.y - screen.w:0.00}..{screen.y + screen.w:0.00}):");
                Bounds? prev = null;
                for (int i = 0; i < rm.characterRewardArray.Length; i++)
                {
                    Transform t = rm.characterRewardArray[i];
                    if (t == null || !t.gameObject.activeInHierarchy)
                    {
                        sb.Append($"\n  row {i}: not shown");
                        continue;
                    }
                    Bounds? b = VisibleBounds(t);
                    if (b == null)
                    {
                        sb.Append($"\n  row {i}: no visible renderers");
                        continue;
                    }
                    Bounds v = b.Value;
                    bool off = v.min.x < screen.x - screen.z || v.max.x > screen.x + screen.z || v.min.y < screen.y - screen.w || v.max.y > screen.y + screen.w;
                    sb.Append($"\n  row {i}: x {v.min.x:0.00}..{v.max.x:0.00}, y {v.min.y:0.00}..{v.max.y:0.00}{(off ? "  OFF SCREEN" : "")}");
                    if (prev != null && prev.Value.Intersects(v))
                    {
                        sb.Append("  (overlaps previous row)");
                    }
                    prev = v;
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Rewards layout check failed: {e.Message}");
            }
        }

        // Sprites and text meshes only (particle systems have huge bounds).
        internal static Bounds? VisibleBounds(Transform t)
        {
            Bounds? b = null;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || !(r is SpriteRenderer || r is MeshRenderer))
                {
                    continue;
                }
                if (r is SpriteRenderer sr && sr.sprite == null)
                {
                    continue;
                }
                if (b == null)
                {
                    b = r.bounds;
                }
                else
                {
                    Bounds x = b.Value;
                    x.Encapsulate(r.bounds);
                    b = x;
                }
            }
            return b;
        }

        // Rows appear one by one (0.15 s apart). CheckAllAssigned only checks rows shown so far (numRewards), so with 8 rows a
        // quick player could pick for every visible row before the last rows appear and the screen would close without them.
        // Wait until every row that is going to be shown is there.
        [HarmonyPatch(typeof(RewardsManager), "CheckAllAssigned")]
        internal static class CheckAllAssignedPatch
        {
            private static bool Prefix(RewardsManager __instance)
            {
                try
                {
                    if (Extra() == 0)
                    {
                        return true;
                    }
                    int expected = ExpectedRows(__instance);
                    int shown = NumRewards(__instance);
                    if (shown < expected)
                    {
                        Plugin.Log.LogInfo($"Rewards: {shown} of {expected} rows shown so far; not closing yet.");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Rewards: CheckAllAssigned guard failed: {e.Message}");
                }
                return true;
            }
        }

        // Same condition ShowRewardsCo uses to decide whether a row is shown.
        private static int ExpectedRows(RewardsManager rm)
        {
            int rows = HeroSlots.VanillaCount + Extra();
            int expected = 0;
            for (int i = 0; i < rows && rm.theTeam != null && i < rm.theTeam.Length; i++)
            {
                Hero h = rm.theTeam[i];
                if (h == null || h.HeroData == null)
                {
                    continue;
                }
                if (AtOManager.Instance.MinigameRewardMode &&
                    (rm.cardsByOrder == null || !rm.cardsByOrder.TryGetValue(i, out string[] cards) || cards == null || cards.Length == 0))
                {
                    continue;
                }
                expected++;
            }
            return expected;
        }
    }
}
