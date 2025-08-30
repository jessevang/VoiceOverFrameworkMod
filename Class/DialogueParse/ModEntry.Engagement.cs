/*
using StardewModdingAPI;
using VoiceOverFrameworkMod;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        private IEnumerable<VoiceEntryTemplate> BuildFromEngagement(
            string characterName, string languageCode, IGameContentHelper content,
            ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var engage = this.GetEngagementDialogueForCharacter(characterName, languageCode, content);
            if (engage == null || engage.Count == 0) return outList;

            foreach (var item in engage)
            {
                string processingKey = item.SourceInfo ?? $"EngagementDialogue/{characterName}";
                string raw = item.RawText ?? "";
                if (string.IsNullOrWhiteSpace(raw)) continue;

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
                        this.Monitor?.Log($"[ENGAGE] + {processingKey} (tk={tk}) p{p.PageIndex} g={p.Gender ?? "na"} -> {path}", LogLevel.Trace);

                    entryNumber++;
                }
            }

            return outList;
        }
    }
}
*/