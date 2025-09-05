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
            int en = entryNumber;

            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            var namedQuoteRegex = new Regex(@"(?:textAboveHead|drawDialogue|message|showText)\s+(\w*)\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var genericQuoteRegex = new Regex(@"""([^""]{4,})""", RegexOptions.Compiled);
            var quickQuestionPrefixRx = new Regex(@"^\s*quickQuestion\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            foreach (var (mapName, eventData) in EnumerateEventSources())
            {
                foreach (var (eventIdRaw, eventScript) in eventData)
                {
                    if (string.IsNullOrWhiteSpace(eventScript))
                        continue;

                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;
                    int speakSerialForTarget = -1;

                    void EmitFromOneEventText(string rawText, string evIdRawLocal)
                    {
                        if (string.IsNullOrWhiteSpace(rawText))
                            return;

                        // split $b as pages for events
                        var segs = DialogueUtil.SplitAndSanitize(rawText, splitBAsPage: true);
                        string cleanId = CleanEventId(evIdRawLocal);

                        foreach (var seg in segs)
                        {
                            speakSerialForTarget++;
                            string tk = BuildEventsTranslationKey(mapName, cleanId, speakSerialForTarget);

                            string fileName = string.IsNullOrEmpty(seg.Gender)
                                ? $"{en}.{ext}"
                                : $"{en}_{seg.Gender}.{ext}";

                            string audioPath = Path.Combine("assets", languageCode, characterName, fileName).Replace('\\', '/');

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
                        }
                    }

                    void ProcessOneCommand(string cmd)
                    {
                        string command = (cmd ?? "").Trim();
                        if (command.Length == 0) return;

                        if (quickQuestionPrefixRx.IsMatch(command) && command.IndexOf("(break)", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var pieces = command.Split(new[] { "(break)" }, StringSplitOptions.None);
                            for (int i = 1; i < pieces.Length; i++)
                            {
                                string normalized = pieces[i].Replace('\\', '/');
                                foreach (var sub in normalized.Split('/'))
                                    ProcessOneCommand(sub);
                            }
                            return;
                        }

                        var speakMatch = speakCommandRegex.Match(command);
                        if (speakMatch.Success)
                        {
                            string speaker = speakMatch.Groups[1].Value;
                            string dialogueText = speakMatch.Groups[2].Value;
                            lastSpeaker = speaker;

                            if (IsCharacterMatch_Event(speaker, characterName))
                                EmitFromOneEventText(dialogueText, eventIdRaw);
                            return;
                        }

                        var namedMatch = namedQuoteRegex.Match(command);
                        if (namedMatch.Success)
                        {
                            string possibleSpeaker = namedMatch.Groups[1].Value;
                            string dialogueText = namedMatch.Groups[2].Value;

                            if (!string.IsNullOrWhiteSpace(possibleSpeaker))
                                lastSpeaker = possibleSpeaker;

                            if (IsCharacterMatch_Event(lastSpeaker, characterName))
                                EmitFromOneEventText(dialogueText, eventIdRaw);
                            return;
                        }

                        if (!string.IsNullOrEmpty(lastSpeaker) && IsCharacterMatch_Event(lastSpeaker, characterName))
                        {
                            var genericMatches = genericQuoteRegex.Matches(command);
                            foreach (Match gm in genericMatches)
                            {
                                string chunk = gm.Groups[1].Value.Trim();
                                if (chunk.Length > 3)
                                    EmitFromOneEventText(chunk, eventIdRaw);
                            }
                        }
                    }

                    foreach (string raw in commands)
                        ProcessOneCommand(raw);
                }
            }

            entryNumber = en;
            return outList;
        }





        private IEnumerable<(string MapName, Dictionary<string, string> Dict)> EnumerateEventSources()
        {
            // per-location assets
            foreach (var loc in Game1.locations)
            {
                if (TryGetLocationEvents(loc, out _, out var eventData) && eventData != null)
                    yield return (loc.NameOrUniqueName ?? loc.Name, eventData);
            }

            // special “locationless” events (Data/Events/Temp)
            Dictionary<string, string> tempDict = null;
            try
            {
                tempDict = Game1.content.Load<Dictionary<string, string>>("Data/Events/Temp");
            }
            catch
            {

            }

            if (tempDict != null && tempDict.Count > 0)
                yield return ("Temp", tempDict);
        }






    }



}
