// VoiceOverFrameworkMod: force %noun → "dragon"; capture/strip adj/place + fixed tokens; add %season capture.

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
        public static class ForceDragonNoun
        {
            public static readonly string[] Value = new[] { "Dragon" };
            public static void Apply() => StardewValley.Dialogue.nouns = Value;
        }


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
            string strippedForMatch = DialogueSanitizerV2.StripChosenWords(currentDisplayedString ?? "", cap);
            string patternKey = CanonDisplay(strippedForMatch);

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
                        missingAudio: true
                    );
                }

                if (!missingAudio)
                    PlayVoiceFromFile(fullPatternPath);

                _lastPlayedLookupKey = finalLookupKey;
                return;
            }

            if (_collectV2Failures)
            {
                V2AddFailure(
                    currentSpeaker?.Name,
                    currentDisplayedString,
                    removable,
                    patternKey,
                    patternKey,
                    matched: false,
                    missingAudio: false
                );
            }

            _lastPlayedLookupKey = finalLookupKey;
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
            cap.HasSeason |= s.IndexOf("%season", StringComparison.OrdinalIgnoreCase) >= 0; // NEW

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

        public static string StripChosenWords(string displayed, Capture cap)
        {
            if (string.IsNullOrWhiteSpace(displayed))
                return string.Empty;

            var removable = GetRemovableWords(cap).ToList();
            if (removable.Count == 0)
                return Normalize(displayed);

            var fixedSet = new HashSet<string>(cap?.FixedTokenWords ?? Enumerable.Empty<string>(),
                                               StringComparer.OrdinalIgnoreCase);

            var fixedParts = removable.Where(w => fixedSet.Contains(w))
                                      .Select(Regex.Escape)
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToArray();

            var poolParts = removable.Where(w => !fixedSet.Contains(w))
                                      .Select(Regex.Escape)
                                      .Distinct(StringComparer.OrdinalIgnoreCase)
                                      .ToArray();

            string s = displayed;

            if (fixedParts.Length > 0)
            {
                var fixedPattern =
                    $@"(?ix)
                       (?: ^ | (?<=\s) )          # start or preceded by whitespace
                       (?: {string.Join("|", fixedParts)} )\b
                     ";

                s = Regex.Replace(
                    s,
                    fixedPattern,
                    " ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace
                );
            }

            if (poolParts.Length > 0)
            {
                var poolPattern = $@"\b(?:{string.Join("|", poolParts)})\b";
                s = Regex.Replace(s, poolPattern, "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

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
            cap.HasSeason |= s.IndexOf("%season", StringComparison.OrdinalIgnoreCase) >= 0; // NEW

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
