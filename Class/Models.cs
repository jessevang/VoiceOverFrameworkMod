using Newtonsoft.Json;

namespace VoiceOverFrameworkMod
{
    public class ModConfig
    {
        public string DefaultLanguage { get; set; } = "en";
        public float MasterVolume { get; set; } = 1.0f;
        public bool turnoffdialoguetypingsound = true;
        public bool FallbackToDefaultIfMissing { get; set; } = false;
        public Dictionary<string, string> SelectedVoicePacks { get; set; } = new();
        public bool developerModeOn { get; set; } = false;
        public bool AutoFixDialogueFromOnLoad { get; set; } = true;

        public int TextStabilizeTicks { set; get; } = 8;

    }

    public class VoicePackManifest
    {
        public string Format { get; set; }
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; }

        [JsonProperty(Order = 1)]
        public IList<VoiceEntry> Entries { get; set; }
    }

    public class VoiceEntry
    {
        public string DialogueText { get; set; }
        public string AudioPath { get; set; }
        public string DialogueFrom { get; set; }
    }

    public class VoicePack
    {
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Language { get; set; }
        public string Character { get; set; }
        public Dictionary<string, string> Entries { get; set; }
        public Dictionary<string, string> EntriesByFrom { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EntriesByTranslationKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> EntriesByDisplayPattern { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public string ContentPackId { get; set; }
        public string ContentPackName { get; set; }
        public string BaseAssetPath { get; set; }

        public int FormatMajor { get; set; } = 1;

    }

    public class VoicePackFile
    {

        public string? Format { get; set; }
        [JsonProperty("VoicePacks")]
        public List<VoicePackManifestTemplate> VoicePacks { get; set; } = new();
    }

    public class VoicePackManifestTemplate
    {
        public string Format { get; set; } = "2.0.0";
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; } = "en";
        public List<VoiceEntryTemplate> Entries { get; set; } = new();
    }

    public class VoiceEntryTemplate
    {

        //V1 Fields
        public string DialogueFrom { get; set; }
        public string DialogueText { get; set; }
        public string AudioPath { get; set; }


        // V2 additions (all optional)
        public string? TranslationKey { get; set; }  // e.g. "Characters/Dialogue/Abigail:danceRejection"
        public int? PageIndex { get; set; }          // 0-based page within that key usually used when dialogue breaks
                                                     // (ended up not needing it since was failing with there are tree forks in dialogue questions and responses that wasn't consistent)
        public string? DisplayPattern { get; set; }  // V2-sanitized text with placeholders used for V2 Dialogue Lookup for match
        public string? GenderVariant { get; set; }   // "male" | "female" | "neutral" (optional) - Though this shows up now, it's really not needed since the final V2 dialogue match algorithm was drastically altered, and the key + gender is only used as a 2nd layer match in case first layer fails

        public string? BranchId { get; set; }
        public string? DialogueTextPortedFromV1 { get; set; }  //Used for Dialogue conversions from V1 to V2 used as referenced so user can manually check and revalidated manually if needed.
    }

}
