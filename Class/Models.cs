using Newtonsoft.Json;


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
    


    public class VoicePack
    {
        public string VoicePackId { get; set; }       
        public string VoicePackName { get; set; }     
        public string Language { get; set; }          
        public string Character { get; set; }        
        public Dictionary<string, string> Entries { get; set; } 

        public string ContentPackId { get; set; }   
        public string ContentPackName { get; set; } 
        public string BaseAssetPath { get; set; }   
    }

  
    public class VoicePackFile
    {
        [JsonProperty("VoicePacks")] 
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

    
}