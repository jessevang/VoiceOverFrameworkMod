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


        //Replaces %noun (random noun) with the word Dragon
        public static class ForceDragonNoun
        {

            public static readonly string[] Value = new[] { Game1.content.LoadString("Strings\\StringsFromCSFiles:Dialogue.cs.699") }; //Dragon
            public static void Apply() => StardewValley.Dialogue.nouns = Value;
        }

        //Replaces %adj (random adjective) with the world Purple
        public static class ForceAdjective
        {
            public static readonly string[] Value = new[] { Game1.content.LoadString("Strings\\StringsFromCSFiles:Dialogue.cs.679") };  //Purple
            public static void Apply() => StardewValley.Dialogue.adjectives = Value;
        }

        //Replaces %place (random place) with the world "Castle Village"
        public static class ForcePlace  //"Castle Village"
        {
            public static readonly string[] Value = new[] { Game1.content.LoadString("Strings\\StringsFromCSFiles:Dialogue.cs.748") };
            public static void Apply() => StardewValley.Dialogue.places = Value;
        }


        private void CheckForLineV2()
        {
            bool isEvent = Game1.currentLocation?.currentEvent != null;
            if (isEvent)
                CheckForEventV2();
            else
                CheckForDialogueV2();
        }
        private void CheckForDialogueV2()
        {
            const bool ENABLE_FUZZY = true;
            const double FUZZY_THRESHOLD = 0.90;
            const int MIN_LEN_FOR_FUZZY = 12;

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

            if (string.IsNullOrWhiteSpace(currentDisplayedString))
                return;

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
            if (currentSpeaker == null)
                return;

            string farmerName = Game1.player.Name;
            string potentialOriginalText = currentDisplayedString;
            if (!string.IsNullOrEmpty(farmerName) && potentialOriginalText.Contains(farmerName))
                potentialOriginalText = potentialOriginalText.Replace(farmerName, "@");

            string sanitizedStep1 = SanitizeDialogueTextV2(potentialOriginalText);
            string finalLookupKey = Regex.Replace(sanitizedStep1, @"#.+?#", "").Trim();
            if (string.IsNullOrWhiteSpace(finalLookupKey))
                return;

            if (string.Equals(finalLookupKey, _lastPlayedLookupKey, StringComparison.Ordinal))
                return;

            var dialogueBox2 = Game1.activeClickableMenu as DialogueBox;
            var d = dialogueBox2?.characterDialogue;
            int pageZero = Math.Max(0, (d?.currentDialogueIndex ?? 0));
            var cap = TokenCaptureStore.Get(d, pageZero, create: true);

            AugmentWithDetectedLegacyTokens(currentDisplayedString, cap);

            var removable = DialogueSanitizerV2.GetRemovableWords(cap).ToList();
            string strippedForMatch = DialogueSanitizerV2.StripForDialogue(currentDisplayedString, cap);


            string patternKey = CanonDisplay(strippedForMatch);

            // ── 1) Exact match
            if (selectedPack != null &&
                selectedPack.FormatMajor >= 2 &&
                selectedPack.Entries != null &&
                selectedPack.Entries.TryGetValue(patternKey, out var relAudioFromPattern) &&
                !string.IsNullOrWhiteSpace(relAudioFromPattern))
            {
                var fullPatternPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, relAudioFromPattern));
                bool missingAudio = !System.IO.File.Exists(fullPatternPath);

                if (_collectV2Failures && missingAudio)
                {
                    V2AddFailure(
                        currentSpeaker?.Name,
                        currentDisplayedString,
                        removable,
                        patternKey,
                        patternKey,
                        matched: true,
                        missingAudio: true,
                        fuzzyAttempted: false
                    );
                }

                if (!missingAudio)
                    PlayVoiceFromFile(fullPatternPath);

                _lastPlayedLookupKey = finalLookupKey;
                return;
            }

            // ── 2) Full-page fallback (strip full page display with same capture context)
            if (selectedPack != null && selectedPack.FormatMajor >= 2 && selectedPack.Entries != null)
            {
                string rawPage = (d != null && pageZero < (d.dialogues?.Count ?? 0)) ? d.dialogues[pageZero].Text : null;
                if (!string.IsNullOrWhiteSpace(rawPage))
                {
                    var segs = DialogueUtil.SplitAndSanitize(rawPage, splitBAsPage: false);
                    if (segs != null && segs.Count > 0)
                    {
                        string fullDisplay = segs[0].Display ?? string.Empty;

                        string strippedFull = DialogueSanitizerV2.StripChosenWords(fullDisplay, cap);
                        string patternKeyFull = CanonDisplay(strippedFull);

                        if (selectedPack.Entries.TryGetValue(patternKeyFull, out var relAudioFull) &&
                            !string.IsNullOrWhiteSpace(relAudioFull))
                        {
                            var fullPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, relAudioFull));
                            bool missingAudio2 = !System.IO.File.Exists(fullPath);

                            if (_collectV2Failures && missingAudio2)
                            {
                                V2AddFailure(
                                    currentSpeaker?.Name,
                                    fullDisplay,
                                    removable,
                                    patternKeyFull,
                                    patternKeyFull,
                                    matched: true,
                                    missingAudio: true,
                                    fuzzyAttempted: false
                                );
                            }

                            if (!missingAudio2)
                                PlayVoiceFromFile(fullPath);

                            _lastPlayedLookupKey = finalLookupKey; // debounce on the visible-line key
                            return;
                        }
                    }
                }
            }

            // ── 3) FUZZY FALLBACK — ONLY if exact + full-page failed
            if (ENABLE_FUZZY && selectedPack != null && selectedPack.FormatMajor >= 2 && selectedPack.Entries != null)
            {
                string liveKey = strippedForMatch; // already stripped
                string bestKey = null;
                string bestRelPath = null;
                double bestScore = 0.0;

                bool attempted = (liveKey?.Length ?? 0) >= MIN_LEN_FOR_FUZZY;
                if (attempted)
                {
                    foreach (var kvp in selectedPack.Entries)
                    {
                        string packKey = kvp.Key;
                        if (string.IsNullOrWhiteSpace(packKey)) continue;

                        double score = SimilarityRatio(liveKey, packKey);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestKey = packKey;
                            bestRelPath = kvp.Value;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(bestRelPath) && bestScore >= FUZZY_THRESHOLD)
                    {
                        var fullPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, bestRelPath));
                        bool missing = !System.IO.File.Exists(fullPath);

                        if (_collectV2Failures)
                        {
                            V2AddFailure(
                                currentSpeaker?.Name,
                                currentDisplayedString,
                                removable,
                                CanonDisplay(liveKey),
                                bestKey,
                                matched: true,
                                missingAudio: missing,
                                fuzzyAttempted: true,
                                fuzzyBestScore: bestScore,
                                fuzzyBestKey: bestKey,
                                fuzzyChosen: true,
                                audioPath: fullPath
                            );
                        }

                        if (!missing)
                            PlayVoiceFromFile(fullPath);

                        _lastPlayedLookupKey = finalLookupKey;
                        return;
                    }
                    else
                    {
                        // ⇒ FUZZY attempted but below threshold: LOG ONCE and EXIT (no double-log)
                        if (_collectV2Failures)
                        {
                            V2AddFailure(
                                currentSpeaker?.Name,
                                currentDisplayedString,
                                removable,
                                CanonDisplay(liveKey),
                                CanonDisplay(liveKey),
                                matched: false,
                                missingAudio: false,
                                fuzzyAttempted: true,
                                fuzzyBestScore: bestScore,
                                fuzzyBestKey: bestKey,
                                fuzzyChosen: false
                            );
                        }
                        _lastPlayedLookupKey = finalLookupKey; 
                        return; //  do not fall through to generic unmatched → avoids double entry
                    }
                }
                // else (not attempted): we fall through to generic unmatched once
            }

            // ── 4) Generic unmatched (no fuzzy or fuzzy disabled/too short)
            if (_collectV2Failures)
            {
                V2AddFailure(
                    currentSpeaker?.Name,
                    currentDisplayedString,
                    removable,
                    patternKey,
                    patternKey,
                    matched: false,
                    missingAudio: false,
                    fuzzyAttempted: false
                );
            }

            _lastPlayedLookupKey = finalLookupKey; // debounce
        }


        private void CheckForEventV2()
        {
            const bool ENABLE_FUZZY = true;
            const double FUZZY_THRESHOLD = 0.86; // looser than dialogue
            const int MIN_LEN_FOR_FUZZY = 12;

            if (Game1.currentLocation == null || Game1.player == null)
            {
                if (lastDialogueText != null) ResetDialogueState();
                _v2_eventSerialBySpeaker.Clear(); _v2_lastEventBase = null; _v2_lastEventPageFingerprint = null;
                return;
            }

            bool isDialogueBoxVisible = Game1.activeClickableMenu is DialogueBox;
            NPC currentSpeaker = Game1.currentSpeaker;

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

            var dialogueBox = Game1.activeClickableMenu as DialogueBox;
            string currentDisplayedString = dialogueBox?.getCurrentString();
            if (string.IsNullOrWhiteSpace(currentDisplayedString))
                return;

            if (currentDisplayedString == lastDialogueText)
                _sameLineStableTicks++;
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

            // Option: skip non-speak (no speaker) to avoid false unmatcheds
            if (currentSpeaker == null)
            {
                // If you want Narrator support later, branch here instead of returning.
                return;
            }

            // Event-side normalization: turn the visible farmer name back into '@' BEFORE stripping
            string displayForMatch = currentDisplayedString ?? string.Empty;
            var farmerName = Utility.FilterUserName(Game1.player?.Name) ?? string.Empty;
            if (!string.IsNullOrEmpty(farmerName))
                displayForMatch = displayForMatch.Replace(farmerName, "@");

            var dialogueBox2 = Game1.activeClickableMenu as DialogueBox;
            var d = dialogueBox2?.characterDialogue;
            int pageZero = Math.Max(0, (d?.currentDialogueIndex ?? 0));
            var cap = TokenCaptureStore.Get(d, pageZero, create: true);

            // Make sure farmer name is always added as a fixed token for events
            if (!string.IsNullOrEmpty(farmerName))
                cap?.AddFixed(farmerName);

            AugmentWithDetectedLegacyTokens(currentDisplayedString, cap);

            var removable = DialogueSanitizerV2.GetRemovableWords(cap).ToList();
            string strippedForMatch = DialogueSanitizerV2.StripForEvent(displayForMatch, cap);
            string patternKey = CanonDisplay(strippedForMatch);

            if (string.IsNullOrWhiteSpace(patternKey))
                return;
            if (string.Equals(patternKey, _lastPlayedLookupKey, StringComparison.Ordinal))
                return;

            VoicePack selectedPack = null;
            if (currentSpeaker != null)
            {
                selectedPack = GetSelectedVoicePack(currentSpeaker.Name);
                if (selectedPack == null || selectedPack.FormatMajor < 2)
                {
                    _lastPlayedLookupKey = patternKey;
                    return;
                }
            }

            // 1) Exact
            if (selectedPack.Entries != null &&
                selectedPack.Entries.TryGetValue(patternKey, out var relAudioFromPattern) &&
                !string.IsNullOrWhiteSpace(relAudioFromPattern))
            {
                var fullPatternPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, relAudioFromPattern));
                if (System.IO.File.Exists(fullPatternPath))
                    PlayVoiceFromFile(fullPatternPath);

                _lastPlayedLookupKey = patternKey;
                return;
            }

            // 2) Full-page fallback (same as dialogue)
            if (selectedPack != null && selectedPack.FormatMajor >= 2 && selectedPack.Entries != null)
            {
                string rawPage = (d != null && pageZero < (d.dialogues?.Count ?? 0)) ? d.dialogues[pageZero].Text : null;
                if (!string.IsNullOrWhiteSpace(rawPage))
                {
                    var segs = DialogueUtil.SplitAndSanitize(rawPage, splitBAsPage: false);
                    if (segs != null && segs.Count > 0)
                    {
                        string fullDisplay = segs[0].Display ?? string.Empty;
                        // normalize farmer name here too
                        if (!string.IsNullOrEmpty(farmerName))
                            fullDisplay = fullDisplay.Replace(farmerName, "@");

                        string strippedFull = DialogueSanitizerV2.StripForEvent(fullDisplay, cap);
                        string patternKeyFull = CanonDisplay(strippedFull);

                        if (selectedPack.Entries.TryGetValue(patternKeyFull, out var relAudioFull) &&
                            !string.IsNullOrWhiteSpace(relAudioFull))
                        {
                            var fullPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, relAudioFull));
                            if (System.IO.File.Exists(fullPath))
                                PlayVoiceFromFile(fullPath);

                            _lastPlayedLookupKey = patternKey;
                            return;
                        }
                    }
                }
            }

            // 3) Fuzzy (looser)
            if (ENABLE_FUZZY && selectedPack?.Entries != null)
            {
                string liveKey = strippedForMatch;
                if ((liveKey?.Length ?? 0) >= MIN_LEN_FOR_FUZZY)
                {
                    string bestKey = null, bestRelPath = null;
                    double bestScore = 0.0;

                    foreach (var kvp in selectedPack.Entries)
                    {
                        var score = SimilarityRatio(liveKey, kvp.Key);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestKey = kvp.Key;
                            bestRelPath = kvp.Value;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(bestRelPath) && bestScore >= FUZZY_THRESHOLD)
                    {
                        var fullPath = PathUtilities.NormalizePath(System.IO.Path.Combine(selectedPack.BaseAssetPath, bestRelPath));
                        if (System.IO.File.Exists(fullPath))
                            PlayVoiceFromFile(fullPath);

                        _lastPlayedLookupKey = patternKey;
                        return;
                    }
                }
            }

            // 4) Unmatched (events)
            if (_collectV2Failures)
            {
                V2AddFailure(
                    currentSpeaker?.Name,
                    currentDisplayedString,
                    removable,
                    patternKey,
                    patternKey,
                    matched: false,
                    missingAudio: false,
                    fuzzyAttempted: true
                );
            }

            _lastPlayedLookupKey = patternKey;
        }


        //Use aglorith for the maining unmatched cases for 90% higher match rate with more  than a certain amount of words to count as a match
        private static string CanonForFuzzy(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return string.Empty;

            // Reuse your NormalizeVisible idea
            s = s.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201B', '\'')
                 .Replace('\u201C', '\"').Replace('\u201D', '\"').Replace('\u201E', '\"')
                 .Replace('\u2013', '-').Replace('\u2014', '-').Replace("\u2026", "...");
            s = Regex.Replace(s, @"\.{4,}", "...");

            // Remove most punctuation for fuzzy comparison
            s = Regex.Replace(s, @"[^\p{L}\p{M}\p{Nd}\s]", ""); // keep letters, marks, digits, spaces

            // Collapse whitespace, lowercase
            s = Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
            return s;
        }

        // Classic Levenshtein distance approach- used to measure distance and similiarity between 2 strings. Used to match strings
        private static int Levenshtein(string a, string b)
        {
            if (a == null) a = string.Empty;
            if (b == null) b = string.Empty;
            int n = a.Length, m = b.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            var prev = new int[m + 1];
            var curr = new int[m + 1];
            for (int j = 0; j <= m; j++) prev[j] = j;

            for (int i = 1; i <= n; i++)
            {
                curr[0] = i;
                char ca = a[i - 1];
                for (int j = 1; j <= m; j++)
                {
                    int cost = (ca == b[j - 1]) ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost
                    );
                }
     
                var tmp = prev; prev = curr; curr = tmp;
            }
            return prev[m];
        }

        private static double SimilarityRatio(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b)) return 1.0;
            var A = CanonForFuzzy(a);
            var B = CanonForFuzzy(b);
            if (A.Length == 0 && B.Length == 0) return 1.0;
            int dist = Levenshtein(A, B);
            int denom = Math.Max(A.Length, B.Length);
            return denom == 0 ? 0.0 : 1.0 - (double)dist / denom;
        }





        private static void AugmentWithDetectedLegacyTokens(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed) || cap == null) return;

            static HashSet<string> ToSet(string[] arr)
                => arr != null && arr.Length > 0
                    ? new HashSet<string>(arr.Select(s => s?.Trim().ToLowerInvariant())
                                              .Where(s => !string.IsNullOrEmpty(s)))
                    : null;

            bool dev = ModEntry.Instance?.Config?.developerModeOn == true;
            var adjSet = ToSet(StardewValley.Dialogue.adjectives);
            var placeSet = ToSet(StardewValley.Dialogue.places);

            var words = Regex.Matches(displayed, @"\b[\p{L}\p{M}A-Za-z'-]+\b")
                             .Cast<Match>()
                             .Select(m => new { raw = m.Value, low = m.Value.ToLowerInvariant() })
                             .ToList();

            foreach (var w in words)
            {
                if (cap.HasAdjToken && adjSet?.Contains(w.low) == true) cap.Add(w.raw);
                if (cap.HasPlaceToken && placeSet?.Contains(w.low) == true) cap.Add(w.raw);
            }

            var mDragon = Regex.Match(displayed, @"\bdragon\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (mDragon.Success)
            {
                cap.HasNounToken = true;
                cap.AddFixed(mDragon.Value);
                if (dev) ModEntry.Instance.Monitor.Log($"[V2-DEBUG][Augment] +noun(forced) '{mDragon.Value}'", LogLevel.Debug);
            }

            if (cap.HasBook && !string.IsNullOrWhiteSpace(Game1.elliottBookName) &&
                displayed.IndexOf(Game1.elliottBookName, StringComparison.OrdinalIgnoreCase) >= 0)
                cap.AddFixed(Game1.elliottBookName);

            if (cap.HasBand && !string.IsNullOrWhiteSpace(Game1.samBandName) &&
                displayed.IndexOf(Game1.samBandName, StringComparison.OrdinalIgnoreCase) >= 0)
                cap.AddFixed(Game1.samBandName);

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
                        cap.AddFixed(whole);
                        if (whole.Length > cap.FarmerHalf.Length)
                            cap.AddFixed(whole.Substring(cap.FarmerHalf.Length));
                    }
                }
            }
            else
            {
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
                        cap.AddFixed(whole);
                        if (whole.Length > half.Length) cap.AddFixed(whole.Substring(half.Length));
                    }
                }
            }

            if (!cap.HasAdjToken && adjSet != null)
                cap.Words.RemoveWhere(w => adjSet.Contains((w ?? "").Trim().ToLowerInvariant()));
            if (!cap.HasPlaceToken && placeSet != null)
                cap.Words.RemoveWhere(w => placeSet.Contains((w ?? "").Trim().ToLowerInvariant()));
        }
    }

    public sealed class Capture
    {
        public readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> FixedTokenWords = new(StringComparer.OrdinalIgnoreCase);

        public bool ExpectFirstHalfPlusName;
        public string FarmerHalf;

        public bool HasAdjToken;
        public bool HasNounToken;
        public bool HasPlaceToken;

        public bool HasAtName;
        public bool HasFarm;
        public bool HasFavorite;
        public bool HasPet;
        public bool HasSpouse;
        public bool HasKid1;
        public bool HasKid2;
        public bool HasBook;
        public bool HasBand;
        public bool HasName;
        public bool HasFirstHalf;

        public bool HasTime;
        public bool HasSeason; // NEW

        public void Add(params string[] xs)
        {
            foreach (var x in xs)
                if (!string.IsNullOrWhiteSpace(x))
                    Words.Add(x.Trim());
        }

        public void AddFixed(params string[] xs)
        {
            foreach (var x in xs)
                if (!string.IsNullOrWhiteSpace(x))
                {
                    var v = x.Trim();
                    Words.Add(v);
                    FixedTokenWords.Add(v);
                }
        }

        public bool AnyFixedTokenPresent() =>
            HasAtName || HasFarm || HasFavorite || HasPet || HasSpouse ||
            HasKid1 || HasKid2 || HasBook || HasBand || HasName || HasFirstHalf || HasSeason || HasTime;
    }

    public static class TokenCaptureStore
    {
        private static readonly ConditionalWeakTable<Dialogue, Dictionary<int, Capture>> _map = new();

        public static void Reset(Dialogue dlg, int pageIndex)
        {
            var dict = _map.GetOrCreateValue(dlg);
            if (!dict.TryGetValue(pageIndex, out var _))
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

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.prepareCurrentDialogueForDisplay))]
    static class Patch_PrepareCurrentDialogueForDisplay_Min
    {
        static void Prefix(Dialogue __instance)
        {
            ForceDragonNoun.Apply();

            var box = Game1.activeClickableMenu as DialogueBox;
            var live = box?.characterDialogue ?? __instance;
            int page = Math.Max(0, live.currentDialogueIndex);

            TokenCaptureStore.Reset(live, page);
            var cap = TokenCaptureStore.Get(live, page);
            if (cap == null) return;

            string raw = (page < live.dialogues.Count ? live.dialogues[page].Text : null) ?? string.Empty;

            cap.HasAdjToken |= raw.IndexOf("%adj", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasNounToken |= raw.IndexOf("%noun", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasPlaceToken |= raw.IndexOf("%place", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasSeason |= raw.IndexOf("%season", StringComparison.OrdinalIgnoreCase) >= 0; // NEW

            if (cap.HasTime)
            {
                string timeStr = Game1.getTimeOfDayString(Game1.timeOfDay);
                if (!string.IsNullOrWhiteSpace(timeStr))
                    cap.AddFixed(timeStr);
            }

            if (cap.HasSeason) // NEW
            {
                string seasonDisplay = !string.IsNullOrWhiteSpace(Game1.CurrentSeasonDisplayName)
                    ? Game1.CurrentSeasonDisplayName
                    : (Game1.currentSeason ?? "");
                if (!string.IsNullOrWhiteSpace(seasonDisplay))
                    cap.AddFixed(seasonDisplay);
            }

            cap.HasAtName |= raw.Contains("@", StringComparison.Ordinal);
            cap.HasFarm |= raw.IndexOf("%farm", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasFavorite |= raw.IndexOf("%favorite", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasPet |= raw.IndexOf("%pet", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasSpouse |= raw.IndexOf("%spouse", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasKid1 |= raw.IndexOf("%kid1", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasKid2 |= raw.IndexOf("%kid2", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasBook |= raw.IndexOf("%book", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasBand |= raw.IndexOf("%band", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasFirstHalf |= raw.IndexOf("%firstnameletter", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasName |= raw.IndexOf("%name", StringComparison.OrdinalIgnoreCase) >= 0;

            var farmer = live.farmer ?? Game1.player;
            if (farmer != null)
            {
                if (cap.HasAtName) cap.AddFixed(Utility.FilterUserName(farmer.Name));
                if (cap.HasFarm) cap.AddFixed(Utility.FilterUserName(farmer.farmName.Value));
                if (cap.HasFavorite) cap.AddFixed(Utility.FilterUserName(farmer.favoriteThing.Value));
                if (cap.HasPet)
                {
                    var petName = farmer.getPetDisplayName();
                    if (!string.IsNullOrWhiteSpace(petName)) cap.AddFixed(petName);
                }

                if (cap.HasSpouse)
                {
                    if (!string.IsNullOrWhiteSpace(farmer.spouse))
                        cap.AddFixed(NPC.GetDisplayName(farmer.spouse));
                    else
                    {
                        var spouseId = farmer.team?.GetSpouse(farmer.UniqueMultiplayerID);
                        if (spouseId.HasValue)
                        {
                            var spouseFarmer = Game1.GetPlayer(spouseId.Value);
                            if (spouseFarmer != null)
                                cap.AddFixed(spouseFarmer.Name);
                        }
                    }
                }

                if (cap.HasKid1 || cap.HasKid2)
                {
                    var kids = farmer.getChildren();
                    if (cap.HasKid1)
                    {
                        var k1 = (kids.Count > 0 ? kids[0]?.displayName : null);
                        if (string.IsNullOrWhiteSpace(k1))
                            cap.AddFixed("the baby", "baby");
                        else
                            cap.AddFixed(k1);
                    }

                    if (cap.HasKid2)
                    {
                        var k2 = (kids.Count > 1 ? kids[1]?.displayName : null);
                        if (string.IsNullOrWhiteSpace(k2))
                            cap.AddFixed("the second baby", "second baby");
                        else
                            cap.AddFixed(k2);
                    }
                }
            }

            if (cap.HasFirstHalf && farmer != null)
            {
                string nm = Utility.FilterUserName(farmer.Name) ?? string.Empty;
                int halfLen = Math.Max(0, nm.Length / 2);
                string half = halfLen > 0 ? nm.Substring(0, halfLen) : string.Empty;

                if (!string.IsNullOrWhiteSpace(half))
                {
                    cap.ExpectFirstHalfPlusName = true;
                    cap.FarmerHalf = half;
                    cap.AddFixed(half);
                }
            }
        }
    }

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
        static void Prefix(ref string str)
        {
            var box = Game1.activeClickableMenu as DialogueBox;
            var live = box?.characterDialogue;
            if (live == null) return;

            int page = Math.Max(0, live.currentDialogueIndex);
            V2DialogueContext.Push(live, page);

            var cap = TokenCaptureStore.Get(live, page, create: true);
            string s = str ?? string.Empty;

            cap.HasAdjToken |= s.IndexOf("%adj", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasNounToken |= s.IndexOf("%noun", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasPlaceToken |= s.IndexOf("%place", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasSeason |= s.IndexOf("%season", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasTime |= s.IndexOf("%time", StringComparison.OrdinalIgnoreCase) >= 0;
            if (cap.HasTime)
            {
                var t = Game1.getTimeOfDayString(Game1.timeOfDay);
                if (!string.IsNullOrWhiteSpace(t))
                    cap.AddFixed(t);
            }

            cap.HasAtName |= s.Contains("@");
            cap.HasFarm |= s.IndexOf("%farm", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasFavorite |= s.IndexOf("%favorite", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasPet |= s.IndexOf("%pet", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasSpouse |= s.IndexOf("%spouse", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasKid1 |= s.IndexOf("%kid1", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasKid2 |= s.IndexOf("%kid2", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasBook |= s.IndexOf("%book", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasBand |= s.IndexOf("%band", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasFirstHalf |= s.IndexOf("%firstnameletter", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasName |= s.IndexOf("%name", StringComparison.OrdinalIgnoreCase) >= 0;

            if (ModEntry.Instance?.Config?.developerModeOn == true)
                ModEntry.Instance.Monitor.Log($"[V2-DEBUG][Replace.Prefix] raw='{s}'", LogLevel.Debug);
        }

        static void Postfix() => V2DialogueContext.Pop();
    }

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.randomName))]
    static class Patch_Dialogue_randomName_Post
    {
        static void Postfix(ref string __result)
        {
            var dlg = V2DialogueContext.CurrentDialogue;
            if (dlg == null) return;

            var cap = TokenCaptureStore.Get(dlg, Math.Max(0, V2DialogueContext.CurrentPage), create: true);
            cap?.AddFixed(__result);
        }
    }

    public static class DialogueSanitizerV2
    {
        public static IEnumerable<string> GetRemovableWords(Capture cap)
        {
            if (cap == null || cap.Words.Count == 0)
                return Enumerable.Empty<string>();

            static HashSet<string> ToSet(string[] arr)
                => arr != null && arr.Length > 0
                    ? new HashSet<string>(arr.Select(s => s?.Trim().ToLowerInvariant())
                                              .Where(s => !string.IsNullOrEmpty(s)))
                    : null;

            var adjSet = ToSet(Dialogue.adjectives);
            var placeSet = ToSet(Dialogue.places);

            var removable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var w in cap.Words)
            {
                if (string.IsNullOrWhiteSpace(w)) continue;
                var lw = w.Trim().ToLowerInvariant();

                if (cap.HasAdjToken && adjSet?.Contains(lw) == true) removable.Add(w);
                if (cap.HasPlaceToken && placeSet?.Contains(lw) == true) removable.Add(w);
            }

            if (cap.FixedTokenWords.Count > 0)
            {
                foreach (var w in cap.FixedTokenWords)
                    if (!string.IsNullOrWhiteSpace(w))
                        removable.Add(w);
            }

            return removable;
        }
        public static string StripForDialogue(string displayed, Capture cap)
        {
            // Dialogue already goes through normal token replacement; no @ normalization needed.
            return StripChosenWordsCore(displayed ?? string.Empty, cap);
        }

        public static string StripForEvent(string displayed, Capture cap)
        {
            // Events often show the *visible* farmer name; normalize it back to '@' before stripping.
            string s = displayed ?? string.Empty;
            var farmerName = Utility.FilterUserName(Game1.player?.Name) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(farmerName))
                s = s.Replace(farmerName, "@");

            return StripChosenWordsCore(s, cap);
        }


        private static string StripChosenWordsCore(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed))
                return string.Empty;

            var removable = GetRemovableWords(cap).ToList();
            if (removable.Count == 0)
                return Normalize(displayed);

            var fixedSet = new HashSet<string>(cap?.FixedTokenWords ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            var fixedTokensRaw = removable.Where(w => fixedSet.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var poolTokensRaw = removable.Where(w => !fixedSet.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string s = displayed;

            // %farm special-case retained
            if (cap?.HasFarm == true && fixedTokensRaw.Count > 0)
            {
                string farmWord = null;
                foreach (var tok in fixedTokensRaw)
                {
                    if (string.IsNullOrWhiteSpace(tok)) continue;
                    var rx = new Regex($@"(?<!\S){Regex.Escape(tok)}(?=\s+Farm\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (rx.IsMatch(s))
                    {
                        farmWord = tok;
                        s = rx.Replace(s, "");
                        break;
                    }
                }
                if (!string.IsNullOrWhiteSpace(farmWord))
                {
                    fixedTokensRaw.RemoveAll(t => t.Equals(farmWord, StringComparison.OrdinalIgnoreCase));
                    poolTokensRaw.RemoveAll(t => t.Equals(farmWord, StringComparison.OrdinalIgnoreCase));
                }
            }

            // >>> Strong fixed-token removal: sort by length + relaxed left boundary
            if (fixedTokensRaw.Count > 0)
            {
                fixedTokensRaw = fixedTokensRaw.OrderByDescending(t => t?.Length ?? 0).ToList();

                var fixedPattern =
                    $@"(?ix)
            (?: ^ | (?<![\p{{L}}\p{{M}}\p{{Nd}}]) )
            (?: {string.Join("|", fixedTokensRaw.Select(Regex.Escape))} )
            (?!-) \b
            ";

                s = Regex.Replace(
                    s,
                    fixedPattern,
                    " ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace
                );
            }

            if (poolTokensRaw.Count > 0)
            {
                var poolPattern = $@"\b(?:{string.Join("|", poolTokensRaw.Select(Regex.Escape))})(?!-)\b";
                s = Regex.Replace(s, poolPattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            s = Regex.Replace(
                s,
                @"%(?:adj|noun|place|name|spouse|firstnameletter|farm|favorite|kid1|kid2|pet|band|book|season|time)\b",
                "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );

            s = Regex.Replace(s, @"[ ]{2,}", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }

        public static string StripChosenWords(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed))
                return string.Empty;
            
            var removable = GetRemovableWords(cap).ToList();
            if (removable.Count == 0)
                return Normalize(displayed);

            var fixedSet = new HashSet<string>(cap?.FixedTokenWords ?? Enumerable.Empty<string>(),
                                               StringComparer.OrdinalIgnoreCase);

            // Work with RAW tokens first (unescaped), so we can selectively exclude later
            var fixedTokensRaw = removable.Where(w => fixedSet.Contains(w))
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .ToList();

            var poolTokensRaw = removable.Where(w => !fixedSet.Contains(w))
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .ToList();

            string s = displayed;
            
            // --- SPECIAL CASE: %farm ---
            // If this page had %farm, only strip the farm name when it occurs right before " Farm".
            // This preserves legitimate uses of the same word elsewhere (e.g., "really great").
            if (cap?.HasFarm == true && fixedTokensRaw.Count > 0)
            {
                // Find any fixed token that actually appears immediately before " Farm"
                string farmWord = null;
                foreach (var tok in fixedTokensRaw)
                {
                    if (string.IsNullOrWhiteSpace(tok)) continue;
                    var rx = new Regex($@"(?<!\S){Regex.Escape(tok)}(?=\s+Farm\b)",
                                       RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (rx.IsMatch(s))
                    {
                        farmWord = tok;
                        // Replace ONLY in the "X Farm" pair → drop X, keep "Farm"
                        s = rx.Replace(s, "");
                        break;
                    }
                }

                // If we identified a farm-word, exclude it from the generic removal that follows
                if (!string.IsNullOrWhiteSpace(farmWord))
                {
                    fixedTokensRaw.RemoveAll(t => t.Equals(farmWord, StringComparison.OrdinalIgnoreCase));
                    poolTokensRaw.RemoveAll(t => t.Equals(farmWord, StringComparison.OrdinalIgnoreCase));
                }
            }



            if (fixedTokensRaw.Count > 0)
            {

                fixedTokensRaw = fixedTokensRaw
                    .OrderByDescending(t => t?.Length ?? 0)
                    .ToList();

                var fixedPattern =
                            $@"(?ix)
                (?: ^ | (?<![\p{{L}}\p{{M}}\p{{Nd}}]) )
                (?: {string.Join("|", fixedTokensRaw.Select(Regex.Escape))} )
                (?!-) \b
                ";

                s = Regex.Replace(
                    s,
                    fixedPattern,
                    " ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace
                );
            }

            if (poolTokensRaw.Count > 0)
            {
                var poolPattern = $@"\b(?:{string.Join("|", poolTokensRaw.Select(Regex.Escape))})(?!-)\b";
                s = Regex.Replace(s, poolPattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            // Also scrub any unresolved %tokens left on screen (covers %spouse, %kid1, etc.)
            s = Regex.Replace(
                s,
                @"%(?:adj|noun|place|name|spouse|firstnameletter|farm|favorite|kid1|kid2|pet|band|book|season|time)\b",
                "",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            );

            // Tidy spaces
            s = Regex.Replace(s, @"[ ]{2,}", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");

            return s.Trim();
        }




        private static string Normalize(string s)
        {
            s = Regex.Replace(s, @"[ ]{2,}", " ");
            s = Regex.Replace(s, @"\s{2,}", " ");
            return s.Trim();
        }
    }

    static class V2ParseContext
    {
        [ThreadStatic] public static Dialogue Current;
        [ThreadStatic] public static int NextIndex;

        public static void Push(Dialogue dlg) { Current = dlg; NextIndex = 0; }
        public static void Advance() { if (Current != null) NextIndex++; }
        public static void Pop() { Current = null; NextIndex = 0; }
    }

    [HarmonyPatch(typeof(Dialogue), "parseDialogueString")]
    static class Patch_Dialogue_parseDialogueString_Context
    {
        static void Prefix(Dialogue __instance) => V2ParseContext.Push(__instance);
        static void Postfix() => V2ParseContext.Pop();
    }

    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.checkForSpecialCharacters))]
    static class Patch_Dialogue_checkForSpecialCharacters_Flags
    {
        static void Prefix(Dialogue __instance, string str)
        {
            var dlg = V2ParseContext.Current ?? __instance;
            if (dlg == null) return;

            int page = Math.Max(0, V2ParseContext.NextIndex);
            var cap = TokenCaptureStore.Get(dlg, page, create: true);
            if (cap == null) return;

            string s = str ?? string.Empty;

            cap.HasAdjToken |= s.IndexOf("%adj", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasNounToken |= s.IndexOf("%noun", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasPlaceToken |= s.IndexOf("%place", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasSeason |= s.IndexOf("%season", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasTime |= s.IndexOf("%time", StringComparison.OrdinalIgnoreCase) >= 0;


            cap.HasAtName |= s.Contains("@");
            cap.HasFarm |= s.IndexOf("%farm", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasFavorite |= s.IndexOf("%favorite", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasPet |= s.IndexOf("%pet", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasSpouse |= s.IndexOf("%spouse", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasKid1 |= s.IndexOf("%kid1", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasKid2 |= s.IndexOf("%kid2", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasBook |= s.IndexOf("%book", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasBand |= s.IndexOf("%band", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasFirstHalf |= s.IndexOf("%firstnameletter", StringComparison.OrdinalIgnoreCase) >= 0;
            cap.HasName |= s.IndexOf("%name", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void Postfix() => V2ParseContext.Advance();
    }
}
