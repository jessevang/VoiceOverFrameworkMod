using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        /// <summary>
        /// Build entries from Data/ExtraDialogue (and any modded sources your GetExtraDialogueForCharacter exposes),
        /// using the shared DialogueUtil splitter/sanitizer so portraits are kept in DialogueText but removed from DisplayPattern.
        /// </summary>
        private IEnumerable<VoiceEntryTemplate> BuildFromExtraDialogue(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            var extra = this.GetExtraDialogueForCharacter(characterName, languageCode, content);
            if (extra == null || extra.Count == 0)
                return outList;

            foreach (var item in extra)
            {
                // Fallbacks
                string processingKey = item.SourceInfo ?? $"ExtraDialogue/{characterName}";
                string raw = item.RawText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Use the shared dialogue splitter/sanitizer
                var pages = DialogueUtil.SplitAndSanitize(raw);
                if (pages == null || pages.Count == 0)
                    continue;

                // Prefer explicit translation key if provided
                string tk = !string.IsNullOrWhiteSpace(item.TranslationKey) ? item.TranslationKey : processingKey;

                foreach (var seg in pages) // seg: Actor, Display, PageIndex, Gender
                {
                    string genderTail = string.IsNullOrEmpty(seg.Gender) ? "" : "_" + seg.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = seg.Actor,
                        AudioPath = path,
                        TranslationKey = tk,
                        PageIndex = seg.PageIndex,
                        DisplayPattern = seg.Display,
                        GenderVariant = seg.Gender
                    });

                    if (this.Config?.developerModeOn == true)
                        this.Monitor?.Log($"[EXTRA] + {processingKey} (tk={tk}) p{seg.PageIndex} g={seg.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }
    }
}
