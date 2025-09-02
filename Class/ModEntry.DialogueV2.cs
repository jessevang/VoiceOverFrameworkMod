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

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private void CheckForDialogueV2()
        {
            if (Game1.currentLocation == null || Game1.player == null)
            {
                if (lastDialogueText != null) ResetDialogueState();
                _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                return;
            }

            bool isDialogueBoxVisible = Game1.activeClickableMenu is DialogueBox;
            NPC currentSpeaker = Game1.currentSpeaker;

            VoicePack selectedPack = null;
            if (currentSpeaker != null)
            {
                selectedPack = GetSelectedVoicePack(currentSpeaker.Name);
                if (selectedPack == null || selectedPack.FormatMajor < 2)
                {
                    if (wasDialogueUpLastTick) ResetDialogueState();
                    _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                    return;
                }
            }

            if (!isDialogueBoxVisible)
            {
                if (wasDialogueUpLastTick)
                {
                    _dialogueNotVisibleTicks++;
                    if (_dialogueNotVisibleTicks >= DialogueCloseDebounceTicks)
                    {
                        ResetDialogueState();
                        _dialogueNotVisibleTicks = 0;
                        _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                    }
                }
                return;
            }
            else
            {
                _dialogueNotVisibleTicks = 0;
            }

            string currentDisplayedString = null;
            if (isDialogueBoxVisible)
            {
                var dialogueBox = Game1.activeClickableMenu as DialogueBox;
                currentDisplayedString = dialogueBox?.getCurrentString();
            }

            if (!string.IsNullOrWhiteSpace(currentDisplayedString))
            {
                if (currentDisplayedString == lastDialogueText)
                {
                    _sameLineStableTicks++;
                }
                else
                {
                    lastDialogueText = currentDisplayedString;
                    lastSpeakerName = currentSpeaker?.Name;
                    wasDialogueUpLastTick = true;
                    _sameLineStableTicks = 0;
                    _lastPlayedLookupKey = null;
                }

                if (_sameLineStableTicks < Config.TextStabilizeTicks)
                    return;

                if (currentSpeaker != null)
                {
                    string farmerName = Game1.player.Name;
                    string potentialOriginalText = currentDisplayedString;

                    if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                        potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");

                    string sanitizedStep1 = SanitizeDialogueTextV2(potentialOriginalText);
                    string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();

                    if (!string.IsNullOrWhiteSpace(finalLookupKey))
                    {
                        if (string.Equals(finalLookupKey, _lastPlayedLookupKey, StringComparison.Ordinal))
                        {
                            if (Config.developerModeOn)
                                Monitor.Log($"[VOICE V2] Debounced repeat key on same page: '{finalLookupKey}'", LogLevel.Trace);
                            return;
                        }

                        var dialogueBox = Game1.activeClickableMenu as DialogueBox;
                        var d = dialogueBox?.characterDialogue;
                        int pageZero = Math.Max(0, (d?.currentDialogueIndex ?? 0));

                        var cap = TokenCaptureStore.Get(d, pageZero, create: true);
                        AugmentWithDetectedLegacyTokens(currentDisplayedString, cap);

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

                        if (selectedPack != null &&
                            selectedPack.FormatMajor >= 2 &&
                            selectedPack.Entries != null &&
                            selectedPack.Entries.TryGetValue(patternKey, out var relAudioFromPattern) &&
                            !string.IsNullOrWhiteSpace(relAudioFromPattern))
                        {
                            var fullPatternPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, relAudioFromPattern));
                            if (Config.developerModeOn)
                                Monitor.Log($"[V2-DISPLAY] MATCH → playing '{relAudioFromPattern}'", LogLevel.Info);

                            PlayVoiceFromFile(fullPatternPath);
                            _lastPlayedLookupKey = finalLookupKey;
                            return;
                        }

                        if (Config.developerModeOn)
                            Monitor.Log("[V2-RESULT] match=No (display-only lookup; no fallback). Not playing.", LogLevel.Info);

                        _lastPlayedLookupKey = finalLookupKey;
                        return;
                    }
                }
            }
        }

        // Augment capture set from the displayed text by recognizing legacy token outputs
        // Also handles glued %firstnameletter + %name like "JeFilbert", and resolved %book/%band if present.
        private static void AugmentWithDetectedLegacyTokens(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed) || cap == null) return;

            // 1) nouns/places from pools (case-insensitive)
            var nouns = Dialogue.nouns != null ? new HashSet<string>(Dialogue.nouns.Select(s => s.ToLowerInvariant())) : null;
            var places = Dialogue.places != null ? new HashSet<string>(Dialogue.places.Select(s => s.ToLowerInvariant())) : null;

            if ((nouns != null && nouns.Count > 0) || (places != null && places.Count > 0))
            {
                foreach (Match m in Regex.Matches(displayed.ToLowerInvariant(), @"\b[\w'-]+\b"))
                {
                    var w = m.Value;
                    if ((nouns?.Contains(w) ?? false) || (places?.Contains(w) ?? false))
                        cap.Add(w);
                }
            }

            // 2) resolved %book / %band (belt-and-suspenders; also added pre-Replace in Prefix when token detected)
            if (!string.IsNullOrWhiteSpace(Game1.elliottBookName) &&
                displayed.IndexOf(Game1.elliottBookName, StringComparison.OrdinalIgnoreCase) >= 0)
                cap.Add(Game1.elliottBookName);

            if (!string.IsNullOrWhiteSpace(Game1.samBandName) &&
                displayed.IndexOf(Game1.samBandName, StringComparison.OrdinalIgnoreCase) >= 0)
                cap.Add(Game1.samBandName);

            // 3) glued %firstnameletter + %name
            if (cap.ExpectFirstHalfPlusName && !string.IsNullOrWhiteSpace(cap.FarmerHalf))
            {
                string halfEsc = Regex.Escape(cap.FarmerHalf);
                var rx = new Regex(@"\b" + halfEsc + @"[A-Za-z'-]+\b",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

                foreach (Match m in rx.Matches(displayed))
                {
                    string whole = m.Value;
                    if (!string.IsNullOrWhiteSpace(whole))
                    {
                        cap.Add(whole); // e.g., "JeFilbert"
                        if (whole.Length > cap.FarmerHalf.Length)
                        {
                            string suffix = whole.Substring(cap.FarmerHalf.Length);
                            if (!string.IsNullOrWhiteSpace(suffix))
                                cap.Add(suffix); // "Filbert"
                        }
                    }
                }
            }
            else
            {
                // Fallback: even if the hint wasn't set, try stripping FarmerHalf + letters once
                var nm = Utility.FilterUserName(Game1.player?.Name) ?? string.Empty;
                int halfLen = Math.Max(0, nm.Length / 2);
                if (halfLen > 0)
                {
                    string half = nm.Substring(0, halfLen);
                    string halfEsc = Regex.Escape(half);
                    var rxAny = new Regex(@"\b" + halfEsc + @"[\p{L}\p{M}A-Za-z'-]+\b", RegexOptions.CultureInvariant);
                    foreach (Match m in rxAny.Matches(displayed))
                    {
                        string whole = m.Value;
                        cap.Add(whole);
                        if (whole.Length > half.Length) cap.Add(whole.Substring(half.Length));
                    }
                }
            }
        }
    }

    // 0) Per-page capture ------------------------------------------------------
    public sealed class Capture
    {
        public readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase);

        public bool ExpectFirstHalfPlusName;
        public string FarmerHalf;

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
            // Prefer the live dialogue used by the UI to ensure read/write consistency
            var box = Game1.activeClickableMenu as DialogueBox;
            var live = box?.characterDialogue ?? __instance;

            int page = Math.Max(0, live.currentDialogueIndex);
            TokenCaptureStore.Reset(live, page);
            var cap = TokenCaptureStore.Get(live, page);
            if (cap == null) return;

            string raw = (page < live.dialogues.Count ? live.dialogues[page].Text : null) ?? string.Empty;

            // add full pools for tokens present on this page
            void AddPool(string[] arr)
            {
                if (arr == null) return;
                foreach (var s in arr)
                    if (!string.IsNullOrWhiteSpace(s))
                        cap.Add(s);
            }

            if (raw.Contains("%adj", StringComparison.Ordinal)) AddPool(Dialogue.adjectives);
            if (raw.Contains("%noun", StringComparison.Ordinal)) AddPool(Dialogue.nouns);
            if (raw.Contains("%place", StringComparison.Ordinal)) AddPool(Dialogue.places);

            if (raw.Contains("%book", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(Game1.elliottBookName))
                cap.Add(Game1.elliottBookName);

            if (raw.Contains("%band", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(Game1.samBandName))
                cap.Add(Game1.samBandName);

            // Player-dependent values
            var farmer = live.farmer ?? Game1.player;
            if (farmer != null)
            {
                cap.Add(
                    Utility.FilterUserName(farmer.Name),
                    Utility.FilterUserName(farmer.farmName.Value),
                    Utility.FilterUserName(farmer.favoriteThing.Value),
                    farmer.getPetDisplayName()
                );

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

                var kids = farmer.getChildren();
                if (kids.Count > 0) cap.Add(kids[0]?.displayName);
                if (kids.Count > 1) cap.Add(kids[1]?.displayName);
            }

            // detect %firstnameletter + %name for glued removal (e.g., "JeFilbert")
            bool hasFirstHalf = raw.IndexOf("%firstnameletter", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasRandName = raw.IndexOf("%name", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasFirstHalf && hasRandName && farmer != null)
            {
                string nm = Utility.FilterUserName(farmer.Name) ?? string.Empty;
                int halfLen = Math.Max(0, nm.Length / 2);
                string half = halfLen > 0 ? nm.Substring(0, halfLen) : string.Empty;

                if (!string.IsNullOrWhiteSpace(half))
                {
                    cap.ExpectFirstHalfPlusName = true;
                    cap.FarmerHalf = half;
                    cap.Add(half);
                }
            }
        }
    }

    // === replacement context & hooks ==========================================
    static class V2DialogueContext
    {
        [ThreadStatic] public static Dialogue CurrentDialogue;
        [ThreadStatic] public static int CurrentPage;

        public static void Push(Dialogue dlg, int page) { CurrentDialogue = dlg; CurrentPage = page; }
        public static void Pop() { CurrentDialogue = null; CurrentPage = -1; }
    }

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.ReplacePlayerEnteredStrings))]
    static class Patch_Dialogue_Replace_Context
    {
        static void Prefix(Dialogue __instance)
        {
            var box = Game1.activeClickableMenu as DialogueBox;
            var live = box?.characterDialogue ?? __instance;
            int page = Math.Max(0, live.currentDialogueIndex);
            V2DialogueContext.Push(live, page);
        }

        static void Postfix()
        {
            V2DialogueContext.Pop();
        }
    }

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.randomName))]
    static class Patch_Dialogue_randomName_Post
    {
        static void Postfix(ref string __result)
        {
            var dlg = V2DialogueContext.CurrentDialogue;
            if (dlg == null) return;

            var cap = TokenCaptureStore.Get(dlg, Math.Max(0, V2DialogueContext.CurrentPage), create: true);
            cap?.Add(__result);
        }
    }

    // 2) Sanitizer used for comparison -----------------------------------------
    public static class DialogueSanitizerV2
    {
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

            var pattern = $@"\b(?:{string.Join("|", parts)})\b";
            var stripped = Regex.Replace(displayed, pattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return Normalize(stripped);
        }

        private static string Normalize(string s)
        {
            s = Regex.Replace(s, @"[ ]{2,}", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }
    }
}
