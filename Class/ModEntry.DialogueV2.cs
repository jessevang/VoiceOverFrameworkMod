using HarmonyLib;
using StardewValley;
using StardewValley.Characters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

/*
 * DialogueTokenCapture.cs (minimal)
 * ---------------------------------
 * Goal:
 *   Build a stable, per-page "remove list" so we can sanitize the on-screen dialogue
 *   and compare it against Voice Pack DisplayPattern values.
 *
 * What we capture:
 *   - Canonical FIRST choices for legacy %tokens: %adj, %noun, %place (only if present on page)
 *   - Player-dependent values: farmer name, farm name, favorite thing, pet, spouse, kid1, kid2
 *
 * How:
 *   - Prefix on Dialogue.prepareCurrentDialogueForDisplay():
 *       1) Reset the per-page capture list.
 *       2) Peek at the raw page text *before* SDV applies player-string replacement.
 *       3) If %adj/%noun/%place exist, add Dialogue.adjectives[0], Dialogue.nouns[0], Dialogue.places[0]
 *          (lowercased as needed to mirror vanilla output for comparisons).
 *       4) Add farmer/family/pet strings from Game1.player.
 *   - Use DialogueSanitizerV2.StripChosenWords(displayed, capture) when comparing.
 *
 * Notes:
 *   - We do NOT change the game's displayed text; we only build a removal list.
 *   - Capture list is per Dialogue instance + page index, cleared every page.
 */

namespace VoiceOverFrameworkMod
{
    // 0) Per-page capture ------------------------------------------------------
    public sealed class Capture
    {
        public readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase);
        public void Add(params string[] xs)
        {
            foreach (var x in xs)
                if (!string.IsNullOrWhiteSpace(x))
                    Words.Add(x.Trim());
        }
    }

    public static class TokenCaptureStore
    {
        private static readonly ConditionalWeakTable<Dialogue, Dictionary<int, Capture>> _map = new();

        public static void Reset(Dialogue dlg, int pageIndex)
        {
            var dict = _map.GetOrCreateValue(dlg);
            dict[pageIndex] = new Capture();
        }

        public static Capture Get(Dialogue dlg, int pageIndex, bool create = true)
        {
            if (dlg == null) return null;
            var dict = _map.GetOrCreateValue(dlg);
            if (!dict.TryGetValue(pageIndex, out var cap) && create)
                dict[pageIndex] = cap = new Capture();
            return cap;
        }
    }

    // 1)  Harmony: reset + add canonical tokens + player strings --------
    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.prepareCurrentDialogueForDisplay))]
    static class Patch_PrepareCurrentDialogueForDisplay_Min
    {
        static void Prefix(Dialogue __instance)
        {
            // Reset capture for this page
            int page = __instance.currentDialogueIndex;
            TokenCaptureStore.Reset(__instance, page);
            var cap = TokenCaptureStore.Get(__instance, page);
            if (cap == null) return;

            // Raw page text (BEFORE ReplacePlayerEnteredStrings runs later in this method)
            string raw = (page < __instance.dialogues.Count ? __instance.dialogues[page].Text : null) ?? string.Empty;

            // Canonical legacy %tokens present on this page
            // %adj
            if (raw.Contains("%adj", StringComparison.Ordinal))
            {
                var firstAdj = Dialogue.adjectives != null && Dialogue.adjectives.Length > 0 ? Dialogue.adjectives[0] : null;
                if (!string.IsNullOrWhiteSpace(firstAdj))
                    cap.Add(firstAdj.ToLowerInvariant()); // vanilla usually lowers these in output
            }

            // %noun (vanilla German special-cases casing; we normalize to lower for matching)
            if (raw.Contains("%noun", StringComparison.Ordinal))
            {
                var firstNoun = Dialogue.nouns != null && Dialogue.nouns.Length > 0 ? Dialogue.nouns[0] : null;
                if (!string.IsNullOrWhiteSpace(firstNoun))
                    cap.Add(firstNoun.ToLowerInvariant());
            }

            // %place (kept as-is in vanilla; lowercase for robust matching)
            if (raw.Contains("%place", StringComparison.Ordinal))
            {
                var firstPlace = Dialogue.places != null && Dialogue.places.Length > 0 ? Dialogue.places[0] : null;
                if (!string.IsNullOrWhiteSpace(firstPlace))
                    cap.Add(firstPlace.ToLowerInvariant());
            }

            // Player-dependent values (always available; no Harmony needed elsewhere)
            var farmer = __instance.farmer ?? Game1.player;
            if (farmer != null)
            {
                // Farmer / farm / favorite / pet
                cap.Add(
                    Utility.FilterUserName(farmer.Name),
                    Utility.FilterUserName(farmer.farmName.Value),
                    Utility.FilterUserName(farmer.favoriteThing.Value),
                    farmer.getPetDisplayName()
                );

                // Spouse display name (covers NPC or player spouse)
                if (!string.IsNullOrWhiteSpace(farmer.spouse))
                {
                    cap.Add(NPC.GetDisplayName(farmer.spouse));
                }
                else
                {
                    var spouseId = farmer.team?.GetSpouse(farmer.UniqueMultiplayerID);
                    if (spouseId.HasValue)
                    {
                        var spouseFarmer = Game1.GetPlayer(spouseId.Value);
                        if (spouseFarmer != null)
                            cap.Add(spouseFarmer.Name);
                    }
                }

                // Children
                var kids = farmer.getChildren();
                if (kids.Count > 0) cap.Add(kids[0]?.displayName);
                if (kids.Count > 1) cap.Add(kids[1]?.displayName);
            }
        }
    }

    // 2) Sanitizer used for comparison -----------------------------------------
    public static class DialogueSanitizerV2
    {
        /// <summary>
        /// Remove all captured words (canonical tokens + player/family names) from
        /// the displayed dialogue text, then collapse whitespace.
        /// Recall how to use
        /// int pageIdx = dlg.currentDialogueIndex;

        // 1) get the final text shown to the player
        //string displayed = dlg.getCurrentDialogue();   // this is AFTER replacements

        // 2) fetch the capture for this page
        //var cap = TokenCaptureStore.Get(dlg, pageIdx, create: false);

        // 3) sanitize for matching
        //string cleaned = DialogueSanitizerV2.StripChosenWords(displayed, cap);

        /// </summary>
        public static string StripChosenWords(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed))
                return string.Empty;

            if (cap == null || cap.Words.Count == 0)
                return Normalize(displayed);

            var parts = cap.Words
                .Select(Regex.Escape)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (parts.Length == 0)
                return Normalize(displayed);

            // \bWORD\b pattern with case-insensitive matching.
            // This avoids nuking substrings inside other words.
            var pattern = $@"\b(?:{string.Join("|", parts)})\b";
            var stripped = Regex.Replace(displayed, pattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return Normalize(stripped);
        }

        private static string Normalize(string s)
        {
            // collapse whitespace and trim
            s = Regex.Replace(s, @"[ ]{2,}", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}
