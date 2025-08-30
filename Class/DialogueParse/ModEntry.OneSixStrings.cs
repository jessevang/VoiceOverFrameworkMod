/*
using Microsoft.Xna.Framework.Content;
using StardewModdingAPI;
using VoiceOverFrameworkMod;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromOneSixStrings(string characterName, string languageCode, IGameContentHelper content,ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var oneSix = this.GetOneSixStringsDialogueForCharacter(characterName, languageCode, content);
            if (oneSix == null || oneSix.Count == 0) return outList;

            foreach (var item in oneSix)
            {
                // item.SourceInfo already includes the unique key for this entry
                string processingKey = item.SourceInfo ?? $"Strings/1_6_Strings/{characterName}";
                string raw = item.RawText ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // usually one-liners; still sanitize to normalize @ -> {PLAYER}, etc.
                string text = SanitizeInlineTokens(raw);

                string file = $"{entryNumber}.{ext}";
                string path = System.IO.Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                string tk = !string.IsNullOrWhiteSpace(item.TranslationKey) ? item.TranslationKey : processingKey;

                outList.Add(new VoiceEntryTemplate
                {
                    DialogueFrom = processingKey,
                    DialogueText = text,
                    AudioPath = path,
                    TranslationKey = tk,
                    PageIndex = 0,
                    DisplayPattern = text
                });

                if (this.Config?.developerModeOn == true)
                    this.Monitor?.Log($"[1_6] + {processingKey} (tk={tk}) -> {path}", LogLevel.Trace);

                entryNumber++;
            }

            return outList;
        }

        /// <summary>
        /// Load dialogue-like lines from Strings/1_6_Strings for a specific character (language-aware).
        /// We consider an entry to "belong" to the character if the key contains an underscore-delimited
        /// segment that equals the character's name (case-insensitive) or that segment equals the name
        /// with trailing digits (e.g., "Emily2").
        /// Returns List of (RawText, SourceInfo, TranslationKey)
        ///   - SourceInfo: "Strings/1_6_Strings:{Key}"
        ///   - TranslationKey: "Strings/1_6_Strings:{Key}"
        /// </summary>
        private List<(string RawText, string SourceInfo, string TranslationKey)>
            GetOneSixStringsDialogueForCharacter(string characterName, string languageCode, IGameContentHelper gameContent)
        {
            var results = new List<(string, string, string)>();
            try
            {
                bool isEnglish = languageCode.Equals("en", StringComparison.OrdinalIgnoreCase);
                string langSuffix = isEnglish ? "" : $".{languageCode}";

                Dictionary<string, string> dict = null;

                // Try language first, fallback to English
                try
                {
                    dict = gameContent.Load<Dictionary<string, string>>($"Strings/1_6_Strings{langSuffix}");
                }
                catch (ContentLoadException)
                {
                    try { dict = gameContent.Load<Dictionary<string, string>>("Strings/1_6_Strings"); }
                    catch (ContentLoadException) { }
                }
                catch {  }

                if (dict == null || dict.Count == 0)
                    return results;

                // Build alias list (canonical + known nicknames)
                var aliases = GetCharacterAliases(characterName);

                // Match if any underscore-delimited key segment is equal to alias (ignoring trailing digits)
                bool TargetsCharacter(string key)
                {
                    if (string.IsNullOrWhiteSpace(key))
                        return false;

                    var parts = key.Split(new[] { '_' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var rawPart in parts)
                    {
                        // strip trailing digits (e.g., Emily2 -> Emily)
                        var part = System.Text.RegularExpressions.Regex.Replace(rawPart, @"\d+$", "");
                        foreach (var alias in aliases)
                        {
                            if (part.Equals(alias, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                    return false;
                }

                foreach (var kvp in dict)
                {
                    string key = kvp.Key ?? "";
                    string raw = kvp.Value ?? "";

                    if (string.IsNullOrWhiteSpace(raw))
                        continue;

                    if (!TargetsCharacter(key))
                        continue;

                    // Keep the raw text; your central loop will sanitize & paginate later
                    string sourceInfo = $"Strings/1_6_Strings:{key}";
                    string tk = $"Strings/1_6_Strings:{key}";

                    results.Add((raw, sourceInfo, tk));
                }
            }
            catch (Exception ex)
            {
                this.Monitor?.Log($"Error in GetOneSixStringsDialogueForCharacter for {characterName}: {ex.Message}", LogLevel.Trace);
            }

            return results;
        }


        private static List<string> GetCharacterAliases(string characterName)
        {
            var aliases = new List<string> { characterName };

            // minimal nickname mapping used by vanilla content (extend as needed)
            if (characterName.Equals("Abigail", StringComparison.OrdinalIgnoreCase))
                aliases.AddRange(new[] { "Abigail", "Abby" });          // include “Abby” alias

            if (characterName.Equals("Elliott", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Elliott");

            if (characterName.Equals("Harvey", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Harvey");

            if (characterName.Equals("Emily", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Emily");

            if (characterName.Equals("Haley", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Haley");

            if (characterName.Equals("Leah", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Leah");

            if (characterName.Equals("Maru", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Maru");

            if (characterName.Equals("Penny", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Penny");

            if (characterName.Equals("Alex", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Alex");

            if (characterName.Equals("Sam", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Sam");

            if (characterName.Equals("Sebastian", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Sebastian");

            if (characterName.Equals("Shane", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Shane");

            if (characterName.Equals("Caroline", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Caroline");

            if (characterName.Equals("Clint", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Clint");

            if (characterName.Equals("Demetrius", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Demetrius");

            if (characterName.Equals("Evelyn", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Evelyn");

            if (characterName.Equals("George", StringComparison.OrdinalIgnoreCase))
                aliases.Add("George");

            if (characterName.Equals("Gus", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Gus");

            if (characterName.Equals("Jas", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Jas");

            if (characterName.Equals("Jodi", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Jodi");

            if (characterName.Equals("Kent", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Kent");

            if (characterName.Equals("Leo", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Leo");

            if (characterName.Equals("Lewis", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Lewis");

            if (characterName.Equals("Linus", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Linus");

            if (characterName.Equals("Marnie", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Marnie");

            if (characterName.Equals("Pam", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Pam");

            if (characterName.Equals("Pierre", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Pierre");

            if (characterName.Equals("Robin", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Robin");

            if (characterName.Equals("Sandy", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Sandy");

            if (characterName.Equals("Willy", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Willy");

            if (characterName.Equals("Wizard", StringComparison.OrdinalIgnoreCase))
                aliases.Add("Wizard");

            return aliases;
        }

    }
}


*/