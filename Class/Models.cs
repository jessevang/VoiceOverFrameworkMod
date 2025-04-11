using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
using System.Collections.Generic; // Added for Dictionary/List

namespace VoiceOverFrameworkMod
{
    // --- Supporting Class Definitions ---

    public class ModConfig
    {
        public string DefaultLanguage { get; set; } = "en";
        public float MasterVolume { get; set; } = 1.0f;
        public bool turnoffdialoguetypingsound = true;
        public bool FallbackToDefaultIfMissing { get; set; } = false;
        public Dictionary<string, string> SelectedVoicePacks { get; set; } = new();
        public bool developerModeOn {get; set; } = false;

    }

    // Note: Removed VoicePackWrapper as it wasn't used in the provided ModEntry code.
    // Add it back if it's used elsewhere.

    // Note: Renamed VoicePackManifest to VoicePackDefinition for clarity
    // if this represents the structure within the JSON file itself (before loading).
    // If it's an internal representation, keep the name. Choose one consistently.
    // Using VoicePackManifestTemplate based on LoadVoicePacks usage.
     // Original - Seems unused directly in loading logic based on template use
    public class VoicePackManifest
    {
        public string Format { get; set; }
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; }
        public List<VoiceEntry> Entries { get; set; }

        
    }

    public class VoiceEntry
    {
        public string DialogueText { get; set; }
        public string AudioPath { get; set; }
    }
    

    // Internal representation of a loaded voice pack
    public class VoicePack
    {
        public string VoicePackId { get; set; }       // Unique ID of this specific voice definition (from JSON)
        public string VoicePackName { get; set; }     // Display name of this voice definition (from JSON)
        public string Language { get; set; }          // Language code (e.g., "en")
        public string Character { get; set; }         // Character name (e.g., "Abigail")
        public Dictionary<string, string> Entries { get; set; } // Key: Sanitized Dialogue Text, Value: Relative Audio Path

        public string ContentPackId { get; set; }   // Unique ID of the *containing* content pack
        public string ContentPackName { get; set; } // Name of the *containing* content pack
        public string BaseAssetPath { get; set; }   // Absolute base path of the *containing* content pack directory
    }

    // Represents the top-level structure of the output JSON file
    public class VoicePackFile
    {
        [JsonProperty("VoicePacks")] // Use JsonProperty for exact naming if needed
        public List<VoicePackManifestTemplate> VoicePacks { get; set; } = new List<VoicePackManifestTemplate>();
    }

    // Template class for deserializing the voice pack JSON files
    public class VoicePackManifestTemplate
    {
        public string Format { get; set; } = "1.0.0";
        public string VoicePackId { get; set; }
        public string VoicePackName { get; set; }
        public string Character { get; set; }
        public string Language { get; set; } = "en";
        public List<VoiceEntryTemplate> Entries { get; set; } = new List<VoiceEntryTemplate>();
    }

    // Template class for deserializing entries within the JSON
    public class VoiceEntryTemplate
    {
        public string DialogueFrom { get; set; } // Optional: To track origin (Dialogue, Strings/Characters)
        public string DialogueText { get; set; } // The raw text from the game file segment
        public string AudioPath { get; set; }    // Relative path within the content pack
    }

    // Note: Removed VoicePackWrapperTemplate as it wasn't used in the provided ModEntry code.
    // Add it back if needed for loading logic that expects a root "VoicePacks" list.
}