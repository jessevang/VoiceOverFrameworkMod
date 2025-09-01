using StardewModdingAPI;
using StardewValley;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {


        // Loads Data/Events for a location by trying NameOrUniqueName then Name.
        private bool TryGetLocationEvents(GameLocation location, out string assetName, out Dictionary<string, string> eventData)
        {
            eventData = null;
            assetName = null;

            string[] candidates =
            {
                $"Data/Events/{location?.NameOrUniqueName}",
                $"Data/Events/{location?.Name}"
            };

            foreach (var candidate in candidates.Distinct())
            {
                try
                {
                    var dict = Game1.content.Load<Dictionary<string, string>>(candidate);
                    if (dict != null && dict.Count > 0)
                    {
                        assetName = candidate;
                        eventData = dict;
                        return true;
                    }
                }
                catch
                {
                    // ignore and try next
                }
            }

            return false;
        }

        // Compares speakers to the target NPC name (case-insensitive).
        private static bool IsCharacterMatch_Event(string speaker, string targetCharacterName) =>
            !string.IsNullOrWhiteSpace(speaker)
            && !string.IsNullOrWhiteSpace(targetCharacterName)
            && speaker.Equals(targetCharacterName, StringComparison.OrdinalIgnoreCase);

        // Small logger helper
        private string TruncEv(string s, int max = 140)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\n", "\\n");
            return (s.Length <= max) ? s : (s.Substring(0, max) + "…");
        }

        // Remove trailing condition tails from event IDs (e.g., "1/f Abigail 500/p Abigail" -> "1")
        private static string CleanEventId(string rawEventKey)
        {
            if (string.IsNullOrWhiteSpace(rawEventKey))
                return rawEventKey;
            int slash = rawEventKey.IndexOf('/');
            return (slash > 0) ? rawEventKey.Substring(0, slash) : rawEventKey.Trim();
        }

        // Parses keys like we will emit in TranslationKey: "Events/<map>:<id>:sN"
        private static string BuildEventsTranslationKey(string map, string eventIdRaw, int speakSerial)
        {
            string cleanId = CleanEventId(eventIdRaw);
            return $"Events/{map}:{cleanId}:s{speakSerial}";
        }

        // ── Main: emit manifest entries from events, using DialogueUtil for parsing ──
        private IEnumerable<VoiceEntryTemplate> BuildFromEvents(string characterName,string languageCode,IGameContentHelper content,ref int entryNumber,string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            int totalFound = 0;

            // Work on a local copy to avoid capturing the ref parameter in a local function
            int en = entryNumber;

            // Event command regexes
            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled);
            var namedQuoteRegex = new Regex(@"(?:textAboveHead|drawDialogue|message|showText)\s+(\w*)\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var genericQuoteRegex = new Regex(@"""([^""]{4,})""", RegexOptions.Compiled);

           

            foreach (var location in Game1.locations)
            {
                if (!TryGetLocationEvents(location, out string assetName, out Dictionary<string, string> eventData))
                    continue;

                foreach (var (eventIdRaw, eventScript) in eventData)
                {
                    if (string.IsNullOrWhiteSpace(eventScript))
                        continue;

                 
                       

                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;

                    // We emit :sN per *spoken page* for the target NPC
                    int speakSerialForTarget = -1;
                    int cmdIndex = -1;

                    foreach (string raw in commands)
                    {
                        cmdIndex++;
                        string command = (raw ?? "").Trim();
                        if (command.Length == 0) continue;

                        // ---- Case 1: speak <NPC> "..." -------------------------------------------
                        var speakMatch = speakCommandRegex.Match(command);
                        if (speakMatch.Success)
                        {
                            string speaker = speakMatch.Groups[1].Value;
                            string dialogueText = speakMatch.Groups[2].Value;
                            lastSpeaker = speaker;

                            

                            if (IsCharacterMatch_Event(speaker, characterName))
                            {
                                EmitFromOneEventText(dialogueText, location.NameOrUniqueName, eventIdRaw, languageCode, characterName);
                            }
                            continue;
                        }

                        // ---- Case 2: drawDialogue/message/showText "..." ------------------------
                        var namedMatch = namedQuoteRegex.Match(command);
                        if (namedMatch.Success)
                        {
                            string possibleSpeaker = namedMatch.Groups[1].Value;
                            string dialogueText = namedMatch.Groups[2].Value;

                            if (!string.IsNullOrWhiteSpace(possibleSpeaker))
                                lastSpeaker = possibleSpeaker;

                          

                            if (IsCharacterMatch_Event(lastSpeaker, characterName))
                            {
                                EmitFromOneEventText(dialogueText, location.NameOrUniqueName, eventIdRaw, languageCode, characterName);
                            }
                            continue;
                        }

                        // ---- Case 3: generic quoted text under lastSpeaker ----------------------
                        if (!string.IsNullOrEmpty(lastSpeaker) && IsCharacterMatch_Event(lastSpeaker, characterName))
                        {
                            var genericMatches = genericQuoteRegex.Matches(command);
      

                            foreach (Match gm in genericMatches)
                            {
                                string chunk = gm.Groups[1].Value.Trim();
                                if (chunk.Length <= 3) continue;

                                EmitFromOneEventText(chunk, location.NameOrUniqueName, eventIdRaw, languageCode, characterName);
                            }
                        }
                    }

                    // local function (captures 'en', not the ref parameter)
                    void EmitFromOneEventText(string rawText, string mapName, string evIdRawLocal, string lang, string charName)
                    {
                        if (string.IsNullOrWhiteSpace(rawText))
                            return;

                        // IMPORTANT: For events, split '#$b#' into SEPARATE PAGES
                        var segs = DialogueUtil.SplitAndSanitize(rawText, splitBAsPage: true);
                       

                        // Clean event id once for DialogueFrom consistency
                        string cleanId = CleanEventId(evIdRawLocal);

                        foreach (var seg in segs)
                        {
                            // bump serial ONCE per emitted page
                            speakSerialForTarget++;

                            string tk = BuildEventsTranslationKey(mapName, cleanId, speakSerialForTarget);

                            // Build audio file path (append _gender if present to keep files distinct)
                            string fileName = string.IsNullOrEmpty(seg.Gender)
                                ? $"{en}.{ext}"
                                : $"{en}_{seg.Gender}.{ext}";

                            string audioPath = Path.Combine("assets", lang, charName, fileName).Replace('\\', '/');

                            outList.Add(new VoiceEntryTemplate
                            {
                                DialogueFrom = $"Event:{mapName}/{cleanId}:s{speakSerialForTarget}" + (string.IsNullOrEmpty(seg.Gender) ? "" : $"|g={seg.Gender}"),
                                DialogueText = seg.Actor,    // includes portrait tags
                                AudioPath = audioPath,
                                TranslationKey = tk,
                                PageIndex = 0,            // one page per :sN
                                DisplayPattern = seg.Display,  // RAW-ish (see DialogueUtil change)
                                GenderVariant = seg.Gender
                            });

                            en++;
                            totalFound++;
                        }
                    }
                }
            }

            // write back the incremented local counter to the ref parameter
            entryNumber = en;

            return outList;
        }


    }
}
