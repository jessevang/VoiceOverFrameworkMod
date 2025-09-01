using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromGiftTastes(
            string characterName,
            string languageCode,
            IGameContentHelper content,
            ref int entryNumber,
            string ext)
        {
            var outList = new List<VoiceEntryTemplate>();

            var gifts = this.GetGiftTasteDialogueForCharacter(characterName, languageCode, content);
            if (gifts == null || gifts.Count == 0)
                return outList;

            foreach (var item in gifts)
            {
                string processingKey = item.SourceInfo ?? $"NPCGiftTastes/{characterName}";
                string raw = item.RawText ?? string.Empty;
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // Use the shared sanitizer/splitter so behavior matches other builders
                var segs = DialogueUtil.SplitAndSanitize(raw);
                if (segs == null || segs.Count == 0)
                    continue;

                // Prefer explicit TK from collector; fallback to processingKey
                string tk = !string.IsNullOrWhiteSpace(item.TranslationKey)
                    ? item.TranslationKey
                    : processingKey;

                foreach (var seg in segs) // seg has Actor, Display, PageIndex, Gender
                {
                    string genderTail = string.IsNullOrEmpty(seg.Gender) ? "" : "_" + seg.Gender;
                    string file = $"{entryNumber}{genderTail}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = seg.Actor,      // includes {Portrait:...}
                        AudioPath = path,
                        TranslationKey = tk,
                        PageIndex = seg.PageIndex,
                        DisplayPattern = seg.Display,    // portraits removed
                        GenderVariant = seg.Gender
                    });

                    if (this.Config?.developerModeOn == true)
                        this.Monitor?.Log($"[GIFTS] + {processingKey} (tk={tk}) p{seg.PageIndex} g={seg.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }
    }
}
