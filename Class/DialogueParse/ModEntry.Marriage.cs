/*
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using VoiceOverFrameworkMod;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromMarriageDialogue(
        string characterName, string languageCode, IGameContentHelper content,
        ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var marriage = this.GetMarriageDialogueForCharacter(characterName, languageCode, content);
            if (marriage == null || marriage.Count == 0) return outList;

            foreach (var item in marriage)
            {
                string processingKey = item.SourceInfo ?? $"Marriage/{characterName}";
                string raw = item.RawText ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // marriage lines can have #$e# / #$b# and gender variants; use common splitter
                var pages = DialogueSplitAndSanitize(raw);

                foreach (var p in pages)
                {
                    string genderTail = string.IsNullOrEmpty(p.Gender) ? "" : "_" + p.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = System.IO.Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    string tk = !string.IsNullOrWhiteSpace(item.TranslationKey) ? item.TranslationKey : processingKey;

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = p.Text,
                        AudioPath = path,
                        TranslationKey = tk,
                        PageIndex = p.PageIndex,
                        DisplayPattern = p.Text,
                        GenderVariant = p.Gender
                    });

                    if (this.Config?.developerModeOn == true)
                        this.Monitor?.Log($"[MARRIAGE] + {processingKey} (tk={tk}) p{p.PageIndex} g={p.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }

        private static readonly string[] SpouseNames = new[]
{
            "Abigail","Alex","Elliott","Emily","Haley","Harvey","Leah",
            "Maru","Penny","Sam","Sebastian","Shane","Krobus"
        };

        /// <summary>
        /// Load marriage dialogue (language-aware) + marriage dilaogue for a specific spouse.
        /// Merges:
        ///   - Characters/Dialogue/MarriageDialogue(.lang)   [generic]
        ///       * includes generic keys WITHOUT any "_Name" suffix
        ///       * includes generic keys WITH "_{characterName}" suffix only
        ///   - Characters/Dialogue/MarriageDialogue{Name}(.lang)  [per-spouse override]
        ///
        /// TranslationKey:
        ///   - Generic:      "Characters/Dialogue/MarriageDialogue:{Key}"
        ///   - Per-spouse:   "Characters/Dialogue/MarriageDialogue{Name}:{Key}"
        ///
        /// Returns List of (RawText, SourceInfo, TranslationKey)
        ///   - SourceInfo is a readable tag like "Marriage/{Name}/{Key}" (you'll store this in sourceTracking)
        ///   - RawText is unsanitized; your existing Split/Sanitize runs later
        /// </summary>
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetMarriageDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new List<(string, string, string)>();

            bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
            string langSuffix = isEnglish ? "" : $".{languageCode}";

            // 1) Load generic sheet (MarriageDialogue)
            Dictionary<string, string> generic = null;
            try
            {
                try
                {
                    generic = gameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{langSuffix}");
                }
                catch (ContentLoadException)
                {
                    generic = gameContent.Load<Dictionary<string, string>>("Characters/Dialogue/MarriageDialogue");
                }
            }
            catch (ContentLoadException) {  }
            catch (Exception ex) { Monitor.Log($"Error reading MarriageDialogue{langSuffix}: {ex.Message}", LogLevel.Trace); }


            // 2) Load per-spouse sheet (MarriageDialogue{Name})
            Dictionary<string, string> perSpouse = null;
            try
            {
                try
                {
                    perSpouse = gameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{characterName}{langSuffix}");
                }
                catch (ContentLoadException)
                {
                    perSpouse = gameContent.Load<Dictionary<string, string>>($"Characters/Dialogue/MarriageDialogue{characterName}");
                }
            }
            catch (ContentLoadException) { }
            catch (Exception ex) { Monitor.Log($"Error reading MarriageDialogue{characterName}{langSuffix}: {ex.Message}", LogLevel.Trace); }


            // 3) Build merged set (key -> tuple), so per-spouse can override
            var merged = new Dictionary<string, (string Raw, string SourceInfo, string TK)>(StringComparer.OrdinalIgnoreCase);


            // 3a) From generic sheet:
            if (generic != null && generic.Count > 0)
            {
                foreach (var kvp in generic)
                {
                    string key = kvp.Key?.Trim();
                    string raw = kvp.Value;

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    // keep keys that have NO spouse suffix OR have _{characterName}
                    bool endsWithAnySpouse = SpouseNames.Any(n => key.EndsWith("_" + n, StringComparison.OrdinalIgnoreCase));
                    bool endsWithThisSpouse = key.EndsWith("_" + characterName, StringComparison.OrdinalIgnoreCase);

                    if (!endsWithAnySpouse || endsWithThisSpouse)
                    {
                        // generic translation key base
                        string tk = $"Characters/Dialogue/MarriageDialogue:{key}";
                        string src = $"Marriage/{characterName}/{key}";
                        merged[key] = (raw, src, tk);
                    }
                }
            }


            // 3b) Overlay with per-spouse sheet (wins on conflicts)
            if (perSpouse != null && perSpouse.Count > 0)
            {
                foreach (var kvp in perSpouse)
                {
                    string key = kvp.Key?.Trim();
                    string raw = kvp.Value;
                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                        continue;

                    string tk = $"Characters/Dialogue/MarriageDialogue{characterName}:{key}";
                    string src = $"Marriage/{characterName}/{key}";
                    merged[key] = (raw, src, tk); // override/add
                }
            }


            // 4) Emit
            foreach (var kvp in merged.OrderBy(k => k.Key))
                results.Add((kvp.Value.Raw, kvp.Value.SourceInfo, kvp.Value.TK));

            if (Config.developerModeOn)
                Monitor.Log($"[Marriage] {characterName}: {results.Count} merged lines.", LogLevel.Trace);

            return results;
        }



    }


}

*/