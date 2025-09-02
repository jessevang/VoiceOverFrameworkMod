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
                    // ignored (asset may not exist for this location)
                }
            }

            return false;
        }

        // Compares speakers to the target NPC name (case-insensitive).
        private static bool IsCharacterMatch_Event(string speaker, string targetCharacterName) =>
            !string.IsNullOrWhiteSpace(speaker)
            && !string.IsNullOrWhiteSpace(targetCharacterName)
            && speaker.Equals(targetCharacterName, StringComparison.OrdinalIgnoreCase);

        // Remove trailing condition tails from event IDs (e.g., "1/f Abigail 500/p Abigail" -> "1")
        private static string CleanEventId(string rawEventKey)
        {
            if (string.IsNullOrWhiteSpace(rawEventKey))
                return rawEventKey;
            int slash = rawEventKey.IndexOf('/');
            return (slash > 0) ? rawEventKey.Substring(0, slash) : rawEventKey.Trim();
        }

        // Parses keys like we will emit in TranslationKey: "Events/<map>:<id>:sN"
        private static string BuildEventsTranslationKey(string map, string eventIdRaw, int speakSerial) =>
            $"Events/{map}:{CleanEventId(eventIdRaw)}:s{speakSerial}";

        // ── Main: emit manifest entries from events, using DialogueUtil for parsing ──
        private IEnumerable<VoiceEntryTemplate> BuildFromEvents(string characterName, string languageCode, IGameContentHelper content, ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            int totalFound = 0;

            int en = entryNumber;

            // Event command regexes
            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var namedQuoteRegex = new Regex(@"(?:textAboveHead|drawDialogue|message|showText)\s+(\w*)\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var genericQuoteRegex = new Regex(@"""([^""]{4,})""", RegexOptions.Compiled);
            var quickQuestionPrefixRx = new Regex(@"^\s*quickQuestion\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

                    // Local helper to emit one quoted text (already attributed to the right speaker by the caller)
                    void EmitFromOneEventText(string rawText, string mapName, string evIdRawLocal, string lang, string charName)
                    {
                        if (string.IsNullOrWhiteSpace(rawText))
                            return;

                        // IMPORTANT: For events, split '#$b#' into SEPARATE PAGES
                        var segs = DialogueUtil.SplitAndSanitize(rawText, splitBAsPage: true);

                        string cleanId = CleanEventId(evIdRawLocal);

                        foreach (var seg in segs)
                        {
                            speakSerialForTarget++;

                            string tk = BuildEventsTranslationKey(mapName, cleanId, speakSerialForTarget);

                            string fileName = string.IsNullOrEmpty(seg.Gender)
                                ? $"{en}.{ext}"
                                : $"{en}_{seg.Gender}.{ext}";

                            string audioPath = Path.Combine("assets", lang, charName, fileName).Replace('\\', '/');

                            outList.Add(new VoiceEntryTemplate
                            {
                                DialogueFrom = $"Event:{mapName}/{cleanId}:s{speakSerialForTarget}" + (string.IsNullOrEmpty(seg.Gender) ? "" : $"|g={seg.Gender}"),
                                DialogueText = seg.Actor,
                                AudioPath = audioPath,
                                TranslationKey = tk,
                                PageIndex = 0,
                                DisplayPattern = seg.Display,
                                GenderVariant = seg.Gender
                            });

                            en++;
                            totalFound++;
                        }
                    }

                    // Process a single event command or a list of subcommands (normalized to '/')
                    void ProcessOneCommand(string cmd, string mapName)
                    {
                        string command = (cmd ?? "").Trim();
                        if (command.Length == 0)
                            return;

                        // ---- Case 0: quickQuestion … (break) … ----
                        // Branch bodies are inside the *same* command and use backslashes to separate inner commands.
                        if (quickQuestionPrefixRx.IsMatch(command) && command.IndexOf("(break)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // pieces[0] is the choices (e.g., "#A#B"), following pieces are per-branch command lists
                            var pieces = command.Split(new[] { "(break)" }, StringSplitOptions.None);

                            for (int i = 1; i < pieces.Length; i++)
                            {
                                // Normalize inner branch commands: the JSON uses '\' between them, convert to '/'
                                string normalized = pieces[i].Replace('\\', '/');

                                foreach (var sub in normalized.Split('/'))
                                {
                                    // recurse the same single-command logic on subcommands
                                    ProcessOneCommand(sub, mapName);
                                }
                            }
                            return;
                        }

                        // ---- Case 1: speak <NPC> "..." -------------------------------------------
                        var speakMatch = speakCommandRegex.Match(command);
                        if (speakMatch.Success)
                        {
                            string speaker = speakMatch.Groups[1].Value;
                            string dialogueText = speakMatch.Groups[2].Value;
                            lastSpeaker = speaker;

                            if (IsCharacterMatch_Event(speaker, characterName))
                                EmitFromOneEventText(dialogueText, location.NameOrUniqueName, eventIdRaw, languageCode, characterName);
                            return;
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
                                EmitFromOneEventText(dialogueText, location.NameOrUniqueName, eventIdRaw, languageCode, characterName);
                            return;
                        }

                        // ---- Case 3: generic quoted text under lastSpeaker ----------------------
                        if (!string.IsNullOrEmpty(lastSpeaker) && IsCharacterMatch_Event(lastSpeaker, characterName))
                        {
                            var genericMatches = genericQuoteRegex.Matches(command);
                            foreach (Match gm in genericMatches)
                            {
                                string chunk = gm.Groups[1].Value.Trim();
                                if (chunk.Length > 3)
                                    EmitFromOneEventText(chunk, location.NameOrUniqueName, eventIdRaw, languageCode, characterName);
                            }
                        }
                    }

                    // Main pass across the event’s '/'-separated commands
                    foreach (string raw in commands)
                        ProcessOneCommand(raw, location.NameOrUniqueName);
                }
            }

            // write back the incremented local counter to the ref parameter
            entryNumber = en;

            return outList;
        }
    }
}
