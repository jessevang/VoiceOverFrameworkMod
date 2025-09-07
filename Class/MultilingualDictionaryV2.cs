using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        // ===========================
        //   V2: shared caches (outer)
        // ===========================

        // char -> lang -> (TK -> (PageIndex -> relative audio path))
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<int, string>>>> tkToPath
            = new(StringComparer.OrdinalIgnoreCase);

        // cache[Character][DisplayPatternKey] = HashSet of (TK, PageIndex)
        private readonly Dictionary<string, Dictionary<string, HashSet<(string tk, int pageIndex)>>> cache
            = new(StringComparer.OrdinalIgnoreCase);

        // loadedLangs[Character] = set of lang codes already merged (canonical like "en", "ja-jp")
        private readonly Dictionary<string, HashSet<string>> loadedLangs
            = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Dictionary<string, Dictionary<string, Dictionary<int, string>>>> tkPageToDisplayKey
    = new(StringComparer.OrdinalIgnoreCase);

        private MultilingualDictionaryV2 MultilingualV2;

        // ---- Language helpers + test context ---------------------------------------
        static class TestContext
        {
            [ThreadStatic] public static bool IsRunningTests;
        }

        private static bool LanguagesDiffer(VoicePack pack)
        {
            if (pack == null) return false;

            // Canonical short codes like "en", "es-es"
            var gameCanon = CanonGameLang();
            var packCanon = LangOfPack(pack);

            // Compare just the base language ("en" from "en", "es" from "es-es")
            string gameBase = ToBaseLang(gameCanon.Replace('_', '-')) ?? gameCanon;
            string packBase = ToBaseLang(packCanon.Replace('_', '-')) ?? packCanon;

            return !string.Equals(gameBase, packBase, StringComparison.OrdinalIgnoreCase);
        }



        private void ApplyDictionaryV2()
        {
            MultilingualV2 = new MultilingualDictionaryV2(this, Monitor, this.Helper.DirectoryPath);
        }

        private string GetPackDisplayKeyForTKPage(string character, string packLang, string tk, int page)
        {
            packLang = CanonLang(packLang ?? "en");

            if (!tkPageToDisplayKey.TryGetValue(character, out var byLang)) return null;
            if (!byLang.TryGetValue(packLang, out var tkMap)) return null;
            if (tkMap.TryGetValue(tk, out var pageMap) && pageMap != null)
            {
                // prefer exact page, then 0, then any
                if (pageMap.TryGetValue(page, out var key) && !string.IsNullOrWhiteSpace(key))
                    return key;
                if (pageMap.TryGetValue(0, out key) && !string.IsNullOrWhiteSpace(key))
                    return key;
                var any = pageMap.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                if (!string.IsNullOrWhiteSpace(any)) return any;
            }
            return null;
        }

        private bool TryGetAudioByDisplayKey(VoicePack selectedPack, string displayKey, out string relPath)
        {
            relPath = null;
            if (selectedPack?.Entries == null || string.IsNullOrWhiteSpace(displayKey)) return false;

            // 1) direct
            if (selectedPack.Entries.TryGetValue(displayKey, out relPath) && !string.IsNullOrWhiteSpace(relPath))
                return true;

            // 2) your CanonDisplay
            string canon = CanonDisplay(displayKey);
            if (!string.IsNullOrWhiteSpace(canon)
                && !string.Equals(canon, displayKey, StringComparison.Ordinal)
                && selectedPack.Entries.TryGetValue(canon, out relPath)
                && !string.IsNullOrWhiteSpace(relPath))
                return true;

            // 3) tiny punctuation canonicalization
            string alt = SimplePunctCanon(displayKey);
            if (!string.IsNullOrWhiteSpace(alt)
                && !string.Equals(alt, displayKey, StringComparison.Ordinal)
                && selectedPack.Entries.TryGetValue(alt, out relPath)
                && !string.IsNullOrWhiteSpace(relPath))
                return true;

            return false;
        }

        private static string SimplePunctCanon(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            s = s.Replace('\u2018', '\'').Replace('\u2019', '\'')
                 .Replace('\u201C', '"').Replace('\u201D', '"')
                 .Replace("\u2026", "...");
            s = Regex.Replace(s, @"\.{4,}", "...");
            s = Regex.Replace(s, @"\s+([!\?\.,;:])", "$1");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }







        private void OnSaveLoaded_PreWarmDictionaries(object sender, StardewModdingAPI.Events.SaveLoadedEventArgs e)
        {
            try
            {
                string saveId = Constants.SaveFolderName ?? Game1.uniqueIDForThisGame.ToString();
                if (!string.IsNullOrWhiteSpace(_dictLoadedForSaveId) &&
                    string.Equals(_dictLoadedForSaveId, saveId, StringComparison.Ordinal))
                {
                    return;
                }

                string gameLang = CanonGameLang();

                foreach (var kv in SelectedVoicePacks)
                {
                    string character = kv.Key;
                    var pack = GetSelectedVoicePack(character);
                    if (pack == null)
                        continue;

                    string packLang = LangOfPack(pack);
                    bool languagesDiffer = LanguagesDiffer(pack);  // <— was StartsWith(...)

                    if (!languagesDiffer)
                        continue;

                    var langsNeeded = new[] { gameLang, packLang };

                    if (pack.FormatMajor >= 2)
                        MultilingualV2?.EnsureLoaded(character, langsNeeded);
                    else
                        Multilingual?.EnsureLoaded(character, langsNeeded);
                }

                _dictLoadedForSaveId = saveId;

                if (Config?.developerModeOn == true)
                    Monitor.Log($"[Dict] Prewarmed dictionaries for save '{saveId}'.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[Dict] Prewarm failed: {ex}", LogLevel.Warn);
            }
        }

        // Clear caches on return to title
        private void OnReturnedToTitle_ClearDictionaries(object sender, StardewModdingAPI.Events.ReturnedToTitleEventArgs e)
        {
            try
            {
                _dictLoadedForSaveId = null;
                Multilingual?.ClearCache();
                MultilingualV2?.ClearCache();
                if (Config?.developerModeOn == true)
                    Monitor.Log("[Dict] Cleared dictionaries on return to title.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[Dict] Clear failed: {ex}", LogLevel.Warn);
            }
        }

        private void EnsureV2DictionaryLoadedFor(string characterName, VoicePack selectedPack)
        {
            if (string.IsNullOrWhiteSpace(characterName) || selectedPack == null || MultilingualV2 == null)
                return;

            if (!LanguagesDiffer(selectedPack))
                return; // same-language: skip V2 completely

            string gameLang = CanonGameLang();
            string packLang = LangOfPack(selectedPack);
            MultilingualV2.EnsureLoaded(characterName, new[] { gameLang, packLang });
        }



        private static string CanonLang(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "en";
            s = s.Trim().ToLowerInvariant();
            return s switch
            {
                "english" or "en" or "en-us" or "en_gb" or "en-au" => "en",
                "spanish" or "es" or "es-es" => "es-es",
                "chinese" or "zh" or "zh-cn" or "zh_hans" => "zh-cn",
                "japanese" or "ja" or "ja-jp" => "ja-jp",
                "portuguese" or "pt" or "pt-br" => "pt-br",
                "french" or "fr" or "fr-fr" => "fr-fr",
                "korean" or "ko" or "ko-kr" => "ko-kr",
                "italian" or "it" or "it-it" => "it-it",
                "german" or "de" or "de-de" => "de-de",
                "hungarian" or "hu" or "hu-hu" => "hu-hu",
                "russian" or "ru" or "ru-ru" => "ru-ru",
                "turkish" or "tr" or "tr-tr" => "tr-tr",
                _ => s
            };
        }

        private static string CanonGameLang() =>
            LocalizedContentManager.CurrentLanguageCode switch
            {
                LocalizedContentManager.LanguageCode.en => "en",
                LocalizedContentManager.LanguageCode.es => "es-es",
                LocalizedContentManager.LanguageCode.zh => "zh-cn",
                LocalizedContentManager.LanguageCode.ja => "ja-jp",
                LocalizedContentManager.LanguageCode.pt => "pt-br",
                LocalizedContentManager.LanguageCode.fr => "fr-fr",
                LocalizedContentManager.LanguageCode.ko => "ko-kr",
                LocalizedContentManager.LanguageCode.it => "it-it",
                LocalizedContentManager.LanguageCode.de => "de-de",
                LocalizedContentManager.LanguageCode.hu => "hu-hu",
                LocalizedContentManager.LanguageCode.ru => "ru-ru",
                LocalizedContentManager.LanguageCode.tr => "tr-tr",
                _ => "en"
            };

        private static string LangOfPack(VoicePack p) => CanonLang(p?.Language ?? "en");

        private void EnsureRightDictionaryForCharacter(string character, VoicePack selectedPack)
        {
            if (string.IsNullOrWhiteSpace(character) || selectedPack == null) return;

            string gameLang = CanonGameLang();
            string packLang = LangOfPack(selectedPack);

            if (!LanguagesDiffer(selectedPack))
                return;

            var langsNeeded = new[] { gameLang, packLang };

            if (selectedPack.FormatMajor >= 2)
                MultilingualV2?.EnsureLoaded(character, langsNeeded);
            else
                Multilingual?.EnsureLoaded(character, langsNeeded);
        }


        private bool TryResolveAudioPath(string character, string packLang, string tk, int pageIndex, out string relPath)
        {
            relPath = null;
            packLang = CanonLang(packLang ?? "en");

            if (!tkToPath.TryGetValue(character, out var byLang)) return false;
            if (!byLang.TryGetValue(packLang, out var tkMap)) return false;
            if (!tkMap.TryGetValue(tk, out var pageMap) || pageMap == null || pageMap.Count == 0) return false;

            // prefer exact page; then page 0; then any page
            if (!pageMap.TryGetValue(pageIndex, out relPath) &&
                !pageMap.TryGetValue(0, out relPath))
            {
                relPath = pageMap.Values.FirstOrDefault();
            }

            return !string.IsNullOrWhiteSpace(relPath);
        }

        private bool TryToPlayVoiceByTKPages(string character,
                                     IEnumerable<(string tk, int pageIndex)> candidates,
                                     VoicePack selectedPack)
        {
            if (candidates == null || selectedPack == null) return false;

            // same-language? V2 path not needed — bail so normal logic runs
            if (!LanguagesDiffer(selectedPack))
                return false;

            string packLang = LangOfPack(selectedPack);

            foreach (var (tk, page) in candidates)
            {
                // 1) Look up the DisplayKey for the PACK language for (TK,page)
                var packKey = GetPackDisplayKeyForTKPage(character, packLang, tk, page);
                if (Config?.developerModeOn == true)
                    Monitor.Log($"[V2/Resolve] TK='{tk}' page={page} → packKey='{packKey ?? "(null)"}'", LogLevel.Info);

                // 2) Resolve audio via PACK ENTRIES (authoritative for filenames)
                if (!string.IsNullOrWhiteSpace(packKey) &&
                    TryGetAudioByDisplayKey(selectedPack, packKey, out var relFromPack))
                {
                    var full = PathUtilities.NormalizePath(Path.Combine(selectedPack.BaseAssetPath, relFromPack));
                    bool exists = System.IO.File.Exists(full);

                    if (Config?.developerModeOn == true)
                        Monitor.Log($"[V2/Resolve] Audio from PACK: {(exists ? "[FOUND] " : "[MISSING] ")}{full}", LogLevel.Info);

                    if (exists)
                    {
                        PlayVoiceFromFile(full);
                        return true;
                    }
                    else if (_collectV2Failures)
                    {
                        V2AddFailure(character, "(V2 try by TK/page)", null, "", packKey,
                            matched: true, missingAudio: true, audioPath: full);
                    }
                }

                // 3) Optional legacy dict fallback (unchanged)
                if (TryResolveAudioPath(character, packLang, tk, page, out var relFromDict) && !string.IsNullOrWhiteSpace(relFromDict))
                {
                    var fullDict = PathUtilities.NormalizePath(Path.Combine(selectedPack.BaseAssetPath, relFromDict));
                    bool exists = System.IO.File.Exists(fullDict);

                    if (Config?.developerModeOn == true)
                    {
                        Monitor.Log($"[V2/Resolve] Audio from DICT: {(exists ? "[FOUND] " : "[MISSING] ")}{fullDict}", LogLevel.Info);
                        if (!exists)
                            Monitor.Log("[V2/Resolve] Note: dictionary filenames may be outdated (e.g., '35.ogg' vs 'Ported_75.ogg').", LogLevel.Info);
                    }

                    if (exists)
                    {
                        PlayVoiceFromFile(fullDict);
                        return true;
                    }
                }
            }

            return false;
        }




        // ==================================
        //   MultilingualDictionaryV2 (inner)
        // ==================================
        public class MultilingualDictionaryV2
        {
            private readonly ModEntry Mod;
            private readonly IMonitor Monitor;
            private readonly string dictionaryPath;

            public MultilingualDictionaryV2(ModEntry mod, IMonitor monitor, string modFolderPath)
            {
                this.Mod = mod;
                this.Monitor = monitor;
                // Dictionaries live under the framework mod folder:
                // Mods/VoiceOverFramework/Dictionary/V2
                this.dictionaryPath = Path.Combine(modFolderPath, "Dictionary", "V2");
            }

            // Optional helper: load all langs present on disk for a character
            public void LoadAllForCharacter(string character)
            {
                if (string.IsNullOrWhiteSpace(character)) return;

                var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(dictionaryPath, $"{character}_*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var data = JsonConvert.DeserializeObject<VoicePackFile>(json);
                        if (data?.VoicePacks == null) continue;

                        foreach (var p in data.VoicePacks)
                        {
                            var lang = CanonLang(p?.Language ?? "");
                            if (!string.IsNullOrWhiteSpace(lang))
                                langs.Add(lang);
                        }
                    }
                    catch { /* ignore */ }
                }

                if (langs.Count > 0)
                    EnsureLoaded(character, langs);
            }

            public void EnsureLoaded(string character, IEnumerable<string> languagesToInclude)
            {
                if (string.IsNullOrWhiteSpace(character)) return;

                var langs = new HashSet<string>(
                    (languagesToInclude ?? Array.Empty<string>())
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(CanonLang),
                    StringComparer.OrdinalIgnoreCase
                );
                if (langs.Count == 0) return;

                // outer dictionaries (do NOT shadow with inner copies)
                if (!Mod.cache.TryGetValue(character, out var charMap))
                    Mod.cache[character] = charMap = new Dictionary<string, HashSet<(string tk, int pageIndex)>>(StringComparer.OrdinalIgnoreCase);

                if (!Mod.loadedLangs.TryGetValue(character, out var loaded))
                    Mod.loadedLangs[character] = loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (!Mod.tkToPath.TryGetValue(character, out var byLang))
                    Mod.tkToPath[character] = byLang = new Dictionary<string, Dictionary<string, Dictionary<int, string>>>(StringComparer.OrdinalIgnoreCase);

                // only load langs not yet merged
                langs.ExceptWith(loaded);
                if (langs.Count == 0) return;

                int addedPairs = 0;
                int addedPaths = 0;

                foreach (string file in Directory.EnumerateFiles(dictionaryPath, $"{character}_*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var data = JsonConvert.DeserializeObject<VoicePackFile>(json);
                        if (data?.VoicePacks == null) continue;

                        foreach (var pack in data.VoicePacks)
                        {
                            var packLang = CanonLang(pack?.Language ?? "");
                            if (string.IsNullOrWhiteSpace(packLang) || !langs.Contains(packLang)) continue;
                            if (pack?.Entries == null) continue;

                            if (!byLang.TryGetValue(packLang, out var tkMap))
                                byLang[packLang] = tkMap = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

                            foreach (var entry in pack.Entries)
                            {
                                string tk = entry?.TranslationKey ?? string.Empty;
                                int page = entry?.PageIndex ?? 0;
                                string rel = (entry?.AudioPath ?? string.Empty).Replace('\\', '/');
                                string vis = entry?.DisplayPattern ?? entry?.DialogueText ?? string.Empty;

                                // 1) TK -> (PageIndex -> rel path) for this language (kept for logging/back-compat)
                                if (!string.IsNullOrWhiteSpace(tk) && !string.IsNullOrWhiteSpace(rel))
                                {
                                    if (!tkMap.TryGetValue(tk, out var pageMap))
                                        tkMap[tk] = pageMap = new Dictionary<int, string>();

                                    if (!pageMap.ContainsKey(page)) addedPaths++;
                                    pageMap[page] = rel; // last one wins
                                }

                                // 2) FINAL display key (must match runtime CanonDisplay)
                                if (string.IsNullOrWhiteSpace(vis) || string.IsNullOrWhiteSpace(tk))
                                    continue;

                                string key = ModEntry.CanonDisplay(vis);
                                if (string.IsNullOrWhiteSpace(key))
                                    continue;

                                
                                if (!Mod.tkPageToDisplayKey.TryGetValue(character, out var byLangKey))
                                    Mod.tkPageToDisplayKey[character] = byLangKey =
                                        new Dictionary<string, Dictionary<string, Dictionary<int, string>>>(StringComparer.OrdinalIgnoreCase);

                                if (!byLangKey.TryGetValue(packLang, out var tkMap2))
                                    byLangKey[packLang] = tkMap2 =
                                        new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

                                if (!tkMap2.TryGetValue(tk, out var pageMap2))
                                    tkMap2[tk] = pageMap2 = new Dictionary<int, string>();

                                pageMap2[page] = key; // last one wins per (TK,page)

                                // visible DisplayKey -> set of (TK,page)
                                if (!charMap.TryGetValue(key, out var set))
                                    charMap[key] = set = new HashSet<(string tk, int pageIndex)>();

                                if (set.Add((tk, page)))
                                    addedPairs++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"[MultilingualDictionaryV2] Failed loading {file}: {ex.Message}", LogLevel.Warn);
                    }
                }

                foreach (var lang in langs)
                    loaded.Add(lang);

                Monitor.Log(
                    $"[MultilingualDictionaryV2] {character}: +{addedPairs} (TK,Page) pairs; +{addedPaths} TK→Page→Path; " +
                    $"langs loaded [{string.Join(",", loaded)}]; visibleKeys={charMap.Count}",
                    LogLevel.Trace
                );
            }

            // Use the exact final key the caller already built.
            public List<(string tk, int pageIndex)> GetTKPageCandidatesByKey(string character, string displayKey)
            {
                if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(displayKey))
                    return null;

                if (!Mod.cache.TryGetValue(character, out var dict) || dict == null)
                    return null;

                return dict.TryGetValue(displayKey, out var set) && set != null && set.Count > 0
                    ? set.ToList()
                    : null;
            }



            /// <summary>
            /// Given the live on-screen string, return candidate (TranslationKey, PageIndex) pairs.
            /// Matches with minimal normalization (trim + collapse whitespace) only.
            /// </summary>
            public List<(string tk, int pageIndex)> GetTKPageCandidates(string character, string rawDisplayed, bool isEvent = false)
            {
                if (!Mod.cache.TryGetValue(character, out var dict) || dict == null)
                    return null;

                // No sanitizer, no farmer-name nor '@' juggling — templates already did that.
                string key = NormalizeDisplay(rawDisplayed ?? string.Empty);
                if (string.IsNullOrWhiteSpace(key))
                    return null;

                return dict.TryGetValue(key, out var set) && set != null && set.Count > 0
                    ? set.ToList()
                    : null;
            }

            /// <summary>
            /// Legacy wrapper: TKs only (distinct), derived from the (TK,Page) list.
            /// </summary>
            public List<string> GetTranslationKeys(string character, string rawDisplayed, bool isEvent = false)
            {
                var pairs = GetTKPageCandidates(character, rawDisplayed, isEvent);
                return pairs?.Select(p => p.tk).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            public void ClearCache()
            {
                Mod.cache.Clear();
                Mod.loadedLangs.Clear();
                Mod.tkToPath.Clear();
                Mod.tkPageToDisplayKey.Clear(); // NEW
                Monitor.Log("[MultilingualDictionaryV2] Cleared all cached entries.", LogLevel.Trace);
            }


            // --- helpers ---
            private static string NormalizeDisplay(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return string.Empty;
                // keep punctuation as-is; just collapse repeated whitespace & trim
                s = Regex.Replace(s, @"\s{2,}", " ");
                return s.Trim();
            }

            private static string CanonLang(string s) => ModEntry.CanonLang(s);
        }
    }
}
