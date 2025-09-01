using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static VoiceOverFrameworkMod.ModEntry;

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



    public partial class ModEntry : Mod
    {

        private void CheckForDialogueV2()
        {
            // Do nothing if no location/player
            if (Game1.currentLocation == null || Game1.player == null)
            {
                if (lastDialogueText != null) ResetDialogueState(); // reuse V1 reset
                                                                    // also clear event-serial state
                _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                return;
            }

            bool isDialogueBoxVisible = Game1.activeClickableMenu is DialogueBox;
            NPC currentSpeaker = Game1.currentSpeaker;

            // Only proceed if the current speaker has a selected V2 pack
            VoicePack selectedPack = null;
            if (currentSpeaker != null)
            {
                selectedPack = GetSelectedVoicePack(currentSpeaker.Name);
                if (selectedPack == null || selectedPack.FormatMajor < 2)
                {
                    // Not a V2 speaker/pack — let your existing V1 pipeline handle it
                    if (wasDialogueUpLastTick) ResetDialogueState();
                    // clear event-serial state
                    _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                    return;
                }
            }

            // --- Debounce transient close between pages ---
            if (!isDialogueBoxVisible)
            {
                if (wasDialogueUpLastTick)
                {
                    _dialogueNotVisibleTicks++;
                    if (_dialogueNotVisibleTicks >= DialogueCloseDebounceTicks) // require consecutive ticks before reset
                    {
                        ResetDialogueState();
                        _dialogueNotVisibleTicks = 0;
                        // clear event-serial state
                        _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                    }
                }
                return; // nothing to do this tick
            }
            else
            {
                _dialogueNotVisibleTicks = 0; // visible, clear debounce
            }

            string currentDisplayedString = null;
            if (isDialogueBoxVisible)
            {
                var dialogueBox = Game1.activeClickableMenu as DialogueBox;
                currentDisplayedString = dialogueBox?.getCurrentString();
            }

            // --- Text stabilization ---
            bool newPageDetectedThisTick = false;
            if (!string.IsNullOrWhiteSpace(currentDisplayedString))
            {
                if (currentDisplayedString == lastDialogueText)
                {
                    _sameLineStableTicks++;
                }
                else
                {
                    // new text/page detected
                    lastDialogueText = currentDisplayedString;  // reuse V1 state
                    lastSpeakerName = currentSpeaker?.Name;     // reuse V1 state
                    wasDialogueUpLastTick = true;               // reuse V1 state
                    _sameLineStableTicks = 0;
                    _lastPlayedLookupKey = null;                // allow one play for this page
                    newPageDetectedThisTick = true;
                }

                if (_sameLineStableTicks < Config.TextStabilizeTicks) // wait more ticks for stability
                    return;

                if (currentSpeaker != null)
                {
                    string farmerName = Game1.player.Name;
                    string potentialOriginalText = currentDisplayedString;

                    // normalize farmer name to '@' - same assumption as V1
                    if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                        potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");

                    // --- V2 sanitizer for the fallback text key ---
                    string sanitizedStep1 = SanitizeDialogueTextV2(potentialOriginalText);

                    // Remove inline #...# emote/meta after sanitization
                    string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();

                    var currentLanguageCode = LocalizedContentManager.CurrentLanguageCode;
                    string gameLanguage = currentLanguageCode.ToString();
                    string characterName = currentSpeaker.Name;
                    string voicePackLanguage = GetVoicePackLanguageForCharacter(characterName);

                    if (!string.IsNullOrWhiteSpace(finalLookupKey))
                    {
                        // Prevent replay on same page
                        if (string.Equals(finalLookupKey, _lastPlayedLookupKey, StringComparison.Ordinal))
                        {
                            if (Config.developerModeOn)
                                Monitor.Log($"[VOICE V2] Debounced repeat key on same page: '{finalLookupKey}'", LogLevel.Trace);
                            return;
                        }

                        // Build context for TK candidates & fallback
                        var dialogueBox = Game1.activeClickableMenu as DialogueBox;
                        var d = dialogueBox?.characterDialogue;

                        // ===== NEW: build/augment capture set for this page, then strip the displayed text =====
                        int pageZero = Math.Max(0, (d?.currentDialogueIndex ?? 0));
                        var cap = TokenCaptureStore.Get(d, pageZero, create: true);           // ensure a capture exists
                        AugmentWithDetectedLegacyTokens(currentDisplayedString, cap);         // add detected %noun/%place from display
                        string strippedForMatch = DialogueSanitizerV2.StripChosenWords(currentDisplayedString ?? "", cap);
                        string patternKey = CanonDisplay(strippedForMatch);

                        if (Config.developerModeOn)
                        {
                            var captured = (cap?.Words != null && cap.Words.Count > 0)
                                ? string.Join(", ", cap.Words.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                                : "(none)";
                            Monitor.Log($"[V2-DISPLAY] Dialogue Text:    \"{currentDisplayedString}\"", LogLevel.Info);
                            Monitor.Log($"[V2-DISPLAY] Capture Words:   [{captured}]", LogLevel.Info);
                            Monitor.Log($"[V2-DISPLAY] Stripped Text:    \"{patternKey}\"", LogLevel.Info);
                        }

                        // Try DisplayPattern (text) match FIRST (Format >= 2 packs)
                        if (selectedPack != null && selectedPack.FormatMajor >= 2
                            && selectedPack.Entries != null
                            && selectedPack.Entries.TryGetValue(patternKey, out var relAudioFromPattern))
                        {
                            var fullPatternPath = PathUtilities.NormalizePath(Path.Combine(selectedPack.BaseAssetPath, relAudioFromPattern));
                            if (Config.developerModeOn)
                                Monitor.Log($"[V2-DISPLAY] MATCH → playing '{relAudioFromPattern}'", LogLevel.Info);

                            PlayVoiceFromFile(fullPatternPath);
                            _lastPlayedLookupKey = finalLookupKey; // mark page as played
                            return;
                        }
                        // =====================================================================

                        // Prefer the vanilla translation key (often "Characters/Dialogue/NPC:key"), fallback to temp if present
                        var tk = d?.TranslationKey ?? d?.temporaryDialogueKey;
                        string sourceKey = (tk != null && tk.StartsWith("Characters/Dialogue/", StringComparison.OrdinalIgnoreCase)) ? tk : null;

                        // Festival key if the TK points at 1.6 strings (or other Strings/* we decided to key)
                        string festKey = (tk != null && tk.StartsWith("Strings/1_6_Strings", StringComparison.OrdinalIgnoreCase)) ? tk : null;

                        // Event key (base) if we’re in an event
                        var ev = GetCurrentEvent();
                        string eventBase = ev != null && !IsFestivalEvent(ev) ? BuildEventBaseKeyForCurrent(ev) : null;

                        // ===== NEW: try probe-derived sheet key if the game didn't give us one =====
                        if (string.IsNullOrWhiteSpace(sourceKey) && string.IsNullOrWhiteSpace(festKey))
                        {
                            if (TryGetRecentSheetKey(characterName, out var recentKey))
                            {
                                if (recentKey.StartsWith("Characters/Dialogue/", StringComparison.OrdinalIgnoreCase))
                                {
                                    sourceKey = recentKey;
                                    bool gameAlreadyHasKey = !string.IsNullOrWhiteSpace(d?.TranslationKey) || !string.IsNullOrWhiteSpace(d?.temporaryDialogueKey);
                                    if (!gameAlreadyHasKey)
                                        Monitor.Log($"[V2-LOOKUP] Using probe-derived Characters/Dialogue key: '{recentKey}'", LogLevel.Info);

                                }
                                else if (recentKey.StartsWith("Strings/StringsFromCSFiles:", StringComparison.OrdinalIgnoreCase))
                                {
                                    festKey = recentKey; // handled similarly to other Strings.* keys with :pN
                                    if (Config.developerModeOn)
                                        Monitor.Log($"[V2-LOOKUP] Using probe-derived StringsFromCSFiles key: '{recentKey}'", LogLevel.Info);
                                }
                            }
                        }
                        // ==========================================================================

                        // --- Maintain per-event, per-speaker serial that increments once per displayed page ---
                        int? eventSerial = null;
                        if (!string.IsNullOrWhiteSpace(eventBase))
                        {
                            // reset serial map when event changes
                            if (!string.Equals(_v2_lastEventBase, eventBase, StringComparison.Ordinal))
                            {
                                _v2_eventSerialBySpeaker.Clear();
                                _v2_lastEventBase = eventBase;
                                _v2_lastEventPageFingerprint = null;
                            }

                            string pageFingerprint = $"{eventBase}|{characterName}|{finalLookupKey}";
                            if (!string.Equals(_v2_lastEventPageFingerprint, pageFingerprint, StringComparison.Ordinal))
                            {
                                // first time we see this specific page → bump serial for this speaker
                                string k = characterName;
                                if (!_v2_eventSerialBySpeaker.TryGetValue(k, out int cur))
                                    cur = -1;
                                cur++;
                                _v2_eventSerialBySpeaker[k] = cur;
                                _v2_lastEventPageFingerprint = pageFingerprint;
                            }

                            eventSerial = _v2_eventSerialBySpeaker[characterName];
                        }
                        else
                        {
                            // leaving event scope → clear serial bookkeeping
                            if (_v2_eventSerialBySpeaker.Count > 0)
                            {
                                _v2_eventSerialBySpeaker.Clear();
                                _v2_lastEventBase = null;
                                _v2_lastEventPageFingerprint = null;
                            }
                        }

                        // --- Compose TK candidates (Events: prefer game's exact key; then our :sN; no :pN) ---
                        var tkCandidates = new List<string>(8);

                        // 0) If the game provided an Events/* key (temporaryDialogueKey), try it *first*
                        string eventTk = (tk != null && tk.StartsWith("Events/", StringComparison.OrdinalIgnoreCase)) ? tk : null;
                        if (!string.IsNullOrWhiteSpace(eventTk))
                        {
                            tkCandidates.Add(eventTk);

                            // also try without any trailing ":pN" just in case (normalization)
                            string noPage = Regex.Replace(eventTk, @":p\d+\b", "", RegexOptions.CultureInvariant);
                            if (!string.Equals(noPage, eventTk, StringComparison.Ordinal))
                                tkCandidates.Add(noPage);
                        }

                        // Characters/Dialogue TK (game or probe)
                        if (!string.IsNullOrWhiteSpace(sourceKey))
                        {
                            tkCandidates.Add($"{sourceKey}:p{pageZero}");
                            tkCandidates.Add(sourceKey);
                        }

                        // Event TK via our running serial (fallback path)
                        if (!string.IsNullOrWhiteSpace(eventBase) && eventSerial.HasValue)
                        {
                            int s = eventSerial.Value;

                            // primary
                            tkCandidates.Add($"{eventBase}:s{s}");

                            // neighbors (cover off-by-one caused by old/new split rules)
                            tkCandidates.Add($"{eventBase}:s{Math.Max(0, s - 1)}");
                            tkCandidates.Add($"{eventBase}:s{s + 1}");

                            // ultra-fallback
                            tkCandidates.Add(eventBase);
                        }

                        // Festival / Strings TK (game or probe)
                        if (!string.IsNullOrWhiteSpace(festKey))
                        {
                            tkCandidates.Add($"{festKey}:p{pageZero}");
                            tkCandidates.Add(festKey);
                        }

                        // --- Fast path: try selected pack's TK map directly ---
                        if (gameLanguage == voicePackLanguage && selectedPack != null && selectedPack.FormatMajor >= 2)
                        {
                            // log lookup intent (show the first/primary candidate)
                            if (Config.developerModeOn)
                            {
                                string primaryKey = tkCandidates.FirstOrDefault() ?? "(none)";

                                // determine origin tag
                                string origin =
                                    (d?.TranslationKey ?? d?.temporaryDialogueKey) is string gameTk && !string.IsNullOrWhiteSpace(gameTk)
                                        ? "(game)"
                                        : (!string.IsNullOrWhiteSpace(sourceKey) || !string.IsNullOrWhiteSpace(festKey))
                                            ? "(probe)"
                                            : "(none)";

                                // also include what the game reported, for clarity
                                string gameReported = (d?.TranslationKey ?? d?.temporaryDialogueKey) ?? "(null)";
                                if (!string.IsNullOrEmpty(gameReported))
                                    gameReported = gameReported.Replace('\\', '/');

                                Monitor.Log(
                                    $"[V2-LOOKUP] char='{characterName}' selPack='{selectedPack.VoicePackId}' text='{currentDisplayedString}' | key='{primaryKey}' {origin} | gameTK='{gameReported}'",
                                    LogLevel.Info
                                );
                            }

                            string resolvedRel = null;
                            foreach (var cand in tkCandidates)
                            {
                                if (selectedPack.EntriesByTranslationKey.TryGetValue(cand, out resolvedRel))
                                {
                                    string fullPath = PathUtilities.NormalizePath(Path.Combine(selectedPack.BaseAssetPath, resolvedRel));

                                    if (Config.developerModeOn)
                                        Monitor.Log($"[V2-RESULT] char='{characterName}' pack='{selectedPack.VoicePackName}' format={selectedPack.FormatMajor} match=Yes key='{cand}' path='{fullPath}'", LogLevel.Info);

                                    PlayVoiceFromFile(fullPath);
                                    _lastPlayedLookupKey = finalLookupKey; // mark as played for debounce
                                    return;
                                }
                            }

                            if (Config.developerModeOn)
                                Monitor.Log($"[V2-RESULT] char='{characterName}' pack='{selectedPack.VoicePackName}' format={selectedPack.FormatMajor} match=No → fallback to V2/V1 text", LogLevel.Info);
                        }

                        // --- Build V2 context then try full V2 fallback (templated keys), then V1 text ---
                        var ctx = new VoiceLineContext
                        {
                            Speaker = characterName,
                            SourceKey = sourceKey,     // e.g. Characters/Dialogue/Abigail:Mon (may come from probe)
                            FestivalKey = festKey,     // e.g. Strings/1_6_Strings:..., or probe StringsFromCSFiles:...
                            EventKey = eventBase,      // e.g. Events/SeedShop:1
                                                       // IMPORTANT: do NOT set Page for Events; keep it only for Characters/Dialogue and Strings
                            Page = string.IsNullOrWhiteSpace(eventBase) ? (pageZero + 1) : (int?)null,
                            Gender = Game1.player?.IsMale == true ? "M" : "F",
                        };

                        TryToPlayVoiceV2(characterName, ctx, currentLanguageCode, finalLookupKey);
                        _lastPlayedLookupKey = finalLookupKey; // mark as played for debounce
                    }
                }
            }
            // no immediate reset here; handled by debounce above
        }




        // Augment capture set from the displayed text by recognizing legacy token outputs
        // (e.g., if %noun produced "robot", add "robot" so StripChosenWords can remove it).
        private static void AugmentWithDetectedLegacyTokens(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed) || cap == null) return;

            // Quick index of known nouns/places (lowercased)
            var nouns = Dialogue.nouns != null ? new HashSet<string>(Dialogue.nouns.Select(s => s.ToLowerInvariant())) : null;
            var places = Dialogue.places != null ? new HashSet<string>(Dialogue.places.Select(s => s.ToLowerInvariant())) : null;

            if ((nouns == null || nouns.Count == 0) && (places == null || places.Count == 0))
                return;

            foreach (Match m in Regex.Matches(displayed.ToLowerInvariant(), @"\b[\w'-]+\b"))
            {
                var w = m.Value;
                if ((nouns?.Contains(w) ?? false) || (places?.Contains(w) ?? false))
                    cap.Add(w);
            }
        }


    }

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


        private static void AugmentWithDetectedLegacyTokens(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed) || cap == null) return;

            // build quick lookups
            var nouns = Dialogue.nouns != null ? new HashSet<string>(Dialogue.nouns.Select(s => s.ToLowerInvariant())) : null;
            var places = Dialogue.places != null ? new HashSet<string>(Dialogue.places.Select(s => s.ToLowerInvariant())) : null;
            if ((nouns == null || nouns.Count == 0) && (places == null || places.Count == 0)) return;

            foreach (Match m in Regex.Matches(displayed.ToLowerInvariant(), @"\b[\w'-]+\b"))
            {
                var w = m.Value;
                if ((nouns?.Contains(w) ?? false) || (places?.Contains(w) ?? false))
                    cap.Add(w);
            }
        }





    }
}
