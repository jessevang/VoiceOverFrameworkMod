using StardewModdingAPI;
using StardewValley;
using System.Text.RegularExpressions;
using VoiceOverFrameworkMod;


namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        /*
        private IEnumerable<VoiceEntryTemplate> BuildFromEvents(string characterName, string languageCode, IGameContentHelper content, ref int entryNumber, string ext)
        {
            var outList = new List<VoiceEntryTemplate>();
            var eventRaw = this.GetEventDialogueForCharacter(characterName, languageCode, content);
            if (eventRaw == null) return outList;

            // Track per-event speak index (consistent with your previous approach)
            var eventSpeakCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var eventSpeakAssigned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            string BaseKey(string src) // "Event:Map/Id/..." -> "Events/Map:Id"
            {
                var m = System.Text.RegularExpressions.Regex.Match(src ?? "", @"^Event:(?<map>[^/]+)/(?<id>\d+)");
                if (!m.Success) return null;
                return $"Events/{m.Groups["map"].Value}:{m.Groups["id"].Value}";
            }
            int GetSpeak(string baseKey, string processingKey)
            {
                string k = $"{baseKey}|{processingKey}";
                if (eventSpeakAssigned.TryGetValue(k, out int idx)) return idx;
                int next = eventSpeakCounters.TryGetValue(baseKey, out int cur) ? cur : 0;
                eventSpeakCounters[baseKey] = next + 1;
                eventSpeakAssigned[k] = next;
                return next;
            }

            foreach (var kv in eventRaw)
            {
                string processingKey = kv.Key;    // full "Event:Map/Id/...”
                string raw = kv.Value;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string baseKey = BaseKey(processingKey);
                int speakIdx = baseKey != null ? GetSpeak(baseKey, processingKey) : -1;

                //var pages = EventSanitizer.Split(raw); // you implement: handles ${gender}, $q/$r -> {CHOICE_REPLY}, etc.

                foreach (var page in pages)
                {
                    string tk = baseKey != null ? $"{baseKey}:s{speakIdx}" : null;
                    if (page.BranchIndex.HasValue) tk += $":split{page.BranchIndex.Value}";

                    string file = $"{entryNumber}{(string.IsNullOrEmpty(page.GenderVariant) ? "" : "_" + page.GenderVariant)}.{ext}";
                    string path = Path.Combine("assets", languageCode, characterName, file).Replace('\\', '/');

                    outList.Add(new VoiceEntryTemplate
                    {
                        DialogueFrom = processingKey,
                        DialogueText = page.Text,
                        AudioPath = path,
                        TranslationKey = tk,
                        PageIndex = page.PageIndex,
                        DisplayPattern = page.Text,
                        GenderVariant = page.GenderVariant
                    });

                    entryNumber++;
                }
            }



            return outList;
        }

        */

        // ── Events-only helpers ────────────────────────────────────────────────────
        private sealed class EvSeg { public string Text; public int PageIndex; public string Gender; public int? BranchIndex; }

        private List<EvSeg> EventSplitAndSanitize(string raw)
        {
            var segments = new List<EvSeg>();
            var branches = raw.Contains("~") ? raw.Split('~') : new[] { raw };

            for (int b = 0; b < branches.Length; b++)
            {
                string body = branches[b];

                bool hasChoice = body.IndexOf("$q", StringComparison.OrdinalIgnoreCase) >= 0;

                var pageChunks = body.Split(new[] { "#$e#" }, StringSplitOptions.None);
                for (int i = 0; i < pageChunks.Length; i++)
                {
                    string page = (pageChunks[i] ?? "").Replace("#$b#", "\n").Trim();
                    if (string.IsNullOrEmpty(page)) continue;

                    if (TryEventTopLevelGender(page, out var male, out var female, out var nb))
                    {
                        if (!string.IsNullOrEmpty(male))
                            segments.Add(new EvSeg { Text = SanitizeEventText(male), PageIndex = i, Gender = "male", BranchIndex = (branches.Length > 1 ? b : (int?)null) });
                        if (!string.IsNullOrEmpty(female))
                            segments.Add(new EvSeg { Text = SanitizeEventText(female), PageIndex = i, Gender = "female", BranchIndex = (branches.Length > 1 ? b : (int?)null) });
                        if (!string.IsNullOrEmpty(nb))
                            segments.Add(new EvSeg { Text = SanitizeEventText(nb), PageIndex = i, Gender = "nonbinary", BranchIndex = (branches.Length > 1 ? b : (int?)null) });
                    }
                    else
                    {
                        segments.Add(new EvSeg
                        {
                            Text = SanitizeEventText(page),
                            PageIndex = i,
                            Gender = null,
                            BranchIndex = (branches.Length > 1 ? b : (int?)null)
                        });
                    }
                }

                if (hasChoice)
                {
                    segments.Add(new EvSeg
                    {
                        Text = "{CHOICE_REPLY}",
                        PageIndex = (pageChunks.Length == 0 ? 0 : pageChunks.Length),
                        Gender = null,
                        BranchIndex = (branches.Length > 1 ? b : (int?)null)
                    });
                }
            }

            return segments;
        }

        private string SanitizeEventText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("@", "{PLAYER}");
            // light scrub of leftover control codes (tune as needed)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)\$[a-z]\d*[^ ]*", "");
            return s.Trim();
        }

        private bool TryEventTopLevelGender(string text, out string male, out string female, out string nb)
        {
            male = female = nb = null;
            var m = System.Text.RegularExpressions.Regex.Match(text ?? "", @"\$\{([^{}]+)\}");
            if (!m.Success) return false;

            var parts = m.Groups[1].Value.Split('^');
            if (parts.Length < 2) return false;

            string before = text.Substring(0, m.Index);
            string after = text[(m.Index + m.Length)..];

            male = before + parts[0] + after;
            female = before + parts[1] + after;
            if (parts.Length >= 3) nb = before + parts[2] + after;

            return true;
        }
    

    //get Event Dialogues dynamically so that modded events are also included

        private Dictionary<string, string> GetEventDialogueForCharacter(string targetCharacterName, string languageCode, IGameContentHelper gameContent)
        {
            Monitor.Log($"[EV_GEN] Scanning for event dialogue for '{targetCharacterName}'...", LogLevel.Info);

            var eventDialogue = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // existing regexes
            var speakCommandRegex = new Regex(@"^speak\s+(\w+)\s+""([^""]*)""", RegexOptions.Compiled);
            var namedQuoteRegex = new Regex($@"(?:textAboveHead|drawDialogue|message|showText)\s+(\w*)\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
            var genericQuoteRegex = new Regex(@"""([^""]{4,})""", RegexOptions.Compiled);

            int foundInEventsCount = 0;

            foreach (var location in Game1.locations)
            {
                if (!location.TryGetLocationEvents(out string assetName, out Dictionary<string, string> eventData))
                    continue;

                foreach (var (eventId, eventScript) in eventData)
                {
                    if (string.IsNullOrWhiteSpace(eventScript))
                        continue;

                    if (Config.developerModeOn)
                        Monitor.Log($"[EV_GEN] ▶ Location='{location.NameOrUniqueName}' Event='{eventId}'", LogLevel.Trace);

                    string[] commands = eventScript.Split('/');
                    string lastSpeaker = null;

                    // EVENTS: no :pN, we emit :sN pages.
                    int speakSerialForTarget = -1;

                    int cmdIndex = -1;
                    foreach (string raw in commands)
                    {
                        cmdIndex++;
                        string command = raw.Trim();
                        if (command.Length == 0) continue;

                        // --------------- Case 1: speak <NPC> "..." ---------------
                        var speakMatch = speakCommandRegex.Match(command);
                        if (speakMatch.Success)
                        {
                            string speaker = speakMatch.Groups[1].Value;
                            string dialogueText = speakMatch.Groups[2].Value;
                            lastSpeaker = speaker;

                            if (Config.developerModeOn)
                                Monitor.Log($"[EV_GEN]   [cmd#{cmdIndex}] speak {speaker}: \"{Trunc(dialogueText)}\"", LogLevel.Trace);

                            if (IsCharacterMatch(speaker, targetCharacterName))
                            {
                                bool hasBranch = dialogueText.IndexOf("$q", StringComparison.OrdinalIgnoreCase) >= 0;

                                // Top-level gender fast-path: one page, multiple variants → SAME :sN
                                if (TrySplitTopLevelGender(dialogueText, out string maleRaw, out string femaleRaw, out string nbRaw))
                                {
                                    string maleLine = CleanSingleLine(maleRaw);
                                    string femaleLine = CleanSingleLine(femaleRaw);
                                    string nbLine = CleanSingleLine(nbRaw);

                                    if (!string.IsNullOrEmpty(maleLine) || !string.IsNullOrEmpty(femaleLine) || !string.IsNullOrEmpty(nbLine))
                                    {
                                        speakSerialForTarget++; // bump ONCE for the page
                                        string suffix = $":s{speakSerialForTarget}";

                                        if (!string.IsNullOrEmpty(maleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, maleLine, ref foundInEventsCount, suffix, genderTag: "male");
                                        if (!string.IsNullOrEmpty(femaleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, femaleLine, ref foundInEventsCount, suffix, genderTag: "female");
                                        if (!string.IsNullOrEmpty(nbLine))
                                            AddEventDialogue(eventDialogue, location, eventId, nbLine, ref foundInEventsCount, suffix, genderTag: "nonbinary");

                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]     gender split (speak) → {suffix} (m/f/nb where present)", LogLevel.Trace);
                                    }

                                    // done with this speak command
                                    continue;
                                }

                                // No gender split → split into spoken pages
                                var lines = ExtractVoiceLinesFromDialogue(dialogueText);
                                if (Config.developerModeOn)
                                    Monitor.Log($"[EV_GEN]     gender=NO, $q={hasBranch}, pages={lines.Count}", LogLevel.Trace);

                                foreach (var line in lines)
                                {
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";
                                    LogAdd(eventDialogue, location, eventId, suffix, line, ref foundInEventsCount);
                                }

                                if (hasBranch)
                                {
                                    // one extra page for the immediate post-choice reply
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";
                                    if (Config.developerModeOn)
                                        Monitor.Log($"[EV_GEN]     reserve reply {suffix}", LogLevel.Trace);
                                    LogAdd(eventDialogue, location, eventId, suffix, "{CHOICE_REPLY}", ref foundInEventsCount);
                                }
                            }
                            continue;
                        }

                        // --------------- Case 2: drawDialogue/message/showText "..." ---------------
                        var namedMatch = namedQuoteRegex.Match(command);
                        if (namedMatch.Success)
                        {
                            string possibleSpeaker = namedMatch.Groups[1].Value;
                            string dialogueText = namedMatch.Groups[2].Value;

                            if (!string.IsNullOrWhiteSpace(possibleSpeaker))
                                lastSpeaker = possibleSpeaker;

                            if (Config.developerModeOn)
                                Monitor.Log($"[EV_GEN]   [cmd#{cmdIndex}] {possibleSpeaker}*: \"{Trunc(dialogueText)}\"", LogLevel.Trace);

                            if (IsCharacterMatch(lastSpeaker, targetCharacterName))
                            {
                                // Gender fast-path first (single page with m^f[^nb])
                                if (TrySplitTopLevelGender(dialogueText, out string maleRaw, out string femaleRaw, out string nbRaw))
                                {
                                    string maleLine = CleanSingleLine(maleRaw);
                                    string femaleLine = CleanSingleLine(femaleRaw);
                                    string nbLine = CleanSingleLine(nbRaw);

                                    if (!string.IsNullOrEmpty(maleLine) || !string.IsNullOrEmpty(femaleLine) || !string.IsNullOrEmpty(nbLine))
                                    {
                                        speakSerialForTarget++;
                                        string suffix = $":s{speakSerialForTarget}";

                                        if (!string.IsNullOrEmpty(maleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, maleLine, ref foundInEventsCount, suffix, genderTag: "male");
                                        if (!string.IsNullOrEmpty(femaleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, femaleLine, ref foundInEventsCount, suffix, genderTag: "female");
                                        if (!string.IsNullOrEmpty(nbLine))
                                            AddEventDialogue(eventDialogue, location, eventId, nbLine, ref foundInEventsCount, suffix, genderTag: "nonbinary");

                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]     gender split (named) → {suffix} (m/f/nb where present)", LogLevel.Trace);

                                        continue;
                                    }
                                }

                                // Otherwise, extract pages (may include multiple pages)
                                var pages = ExtractVoicePagesFromDialogue(dialogueText);

                                if (Config.developerModeOn)
                                    Monitor.Log($"[EV_GEN]     pages={pages.Count}", LogLevel.Trace);

                                foreach (var page in pages)
                                {
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";

                                    // Each page can have 1..3 variants (gender) *if* your extractor returns them.
                                    // Since variants here aren't labeled, we store without explicit genderTag.
                                    for (int i = 0; i < page.Variants.Count; i++)
                                    {
                                        string v = page.Variants[i];
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       variant[{i}] -> {suffix}: \"{Trunc(v)}\"", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, suffix, v, ref foundInEventsCount);
                                    }

                                    if (page.ReserveNextSerial)
                                    {
                                        speakSerialForTarget++;
                                        string replyKey = $":s{speakSerialForTarget}";
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       reserve reply {replyKey}", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, replyKey, "{CHOICE_REPLY}", ref foundInEventsCount);
                                    }
                                }
                            }
                            continue;
                        }

                        // --------------- Case 3: Generic quoted text under lastSpeaker ---------------
                        if (!string.IsNullOrEmpty(lastSpeaker) && IsCharacterMatch(lastSpeaker, targetCharacterName))
                        {
                            var genericMatches = genericQuoteRegex.Matches(command);
                            if (genericMatches.Count > 0 && Config.developerModeOn)
                                Monitor.Log($"[EV_GEN]   [cmd#{cmdIndex}] generic under '{lastSpeaker}': found {genericMatches.Count} quoted blocks", LogLevel.Trace);

                            foreach (Match m in genericMatches)
                            {
                                string chunk = m.Groups[1].Value.Trim();
                                if (chunk.Length <= 3) continue;

                                // Gender fast-path if the whole chunk is a single page with ^ split
                                if (TrySplitTopLevelGender(chunk, out string maleRaw, out string femaleRaw, out string nbRaw))
                                {
                                    string maleLine = CleanSingleLine(maleRaw);
                                    string femaleLine = CleanSingleLine(femaleRaw);
                                    string nbLine = CleanSingleLine(nbRaw);

                                    if (!string.IsNullOrEmpty(maleLine) || !string.IsNullOrEmpty(femaleLine) || !string.IsNullOrEmpty(nbLine))
                                    {
                                        speakSerialForTarget++;
                                        string suffix = $":s{speakSerialForTarget}";

                                        if (!string.IsNullOrEmpty(maleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, maleLine, ref foundInEventsCount, suffix, genderTag: "male");
                                        if (!string.IsNullOrEmpty(femaleLine))
                                            AddEventDialogue(eventDialogue, location, eventId, femaleLine, ref foundInEventsCount, suffix, genderTag: "female");
                                        if (!string.IsNullOrEmpty(nbLine))
                                            AddEventDialogue(eventDialogue, location, eventId, nbLine, ref foundInEventsCount, suffix, genderTag: "nonbinary");

                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]     gender split (generic) → {suffix} (m/f/nb where present)", LogLevel.Trace);

                                        continue;
                                    }
                                }

                                // Otherwise, page-extract within the chunk
                                var pages = ExtractVoicePagesFromDialogue(chunk);

                                if (Config.developerModeOn)
                                    Monitor.Log($"[EV_GEN]     generic pages={pages.Count}", LogLevel.Trace);

                                foreach (var page in pages)
                                {
                                    speakSerialForTarget++;
                                    string suffix = $":s{speakSerialForTarget}";

                                    for (int i = 0; i < page.Variants.Count; i++)
                                    {
                                        string v = page.Variants[i];
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       variant[{i}] -> {suffix}: \"{Trunc(v)}\"", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, suffix, v, ref foundInEventsCount);
                                    }

                                    if (page.ReserveNextSerial)
                                    {
                                        speakSerialForTarget++;
                                        string replyKey = $":s{speakSerialForTarget}";
                                        if (Config.developerModeOn)
                                            Monitor.Log($"[EV_GEN]       reserve reply {replyKey}", LogLevel.Trace);
                                        LogAdd(eventDialogue, location, eventId, replyKey, "{CHOICE_REPLY}", ref foundInEventsCount);
                                    }
                                }
                            }
                        }
                    } // foreach command
                } // foreach event
            } // foreach location

            Monitor.Log($"[EV_GEN] Done. Found {foundInEventsCount} event dialogue lines for '{targetCharacterName}'.", LogLevel.Info);
            return eventDialogue;

            // ---- local helpers used by this method ----

            void LogAdd(Dictionary<string, string> dict, GameLocation loc, string evId, string suff, string text, ref int ctr)
            {
                AddEventDialogue(dict, loc, evId, text, ref ctr, suff, genderTag: null);
                if (Config.developerModeOn)
                    Monitor.Log($"[EV_GEN]       add {loc.NameOrUniqueName}/{evId}{suff} → \"{Trunc(text)}\"", LogLevel.Trace);
            }
        }



        // ------- tiny helpers for logging GetEventDialogueForCharacter()-------

        private string Trunc(string s, int max = 140)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\n", "\\n");
            return (s.Length <= max) ? s : (s.Substring(0, max) + "…");
        }

        private void LogAdd(Dictionary<string, string> dict, GameLocation location, string eventId, string suffix, string text, ref int counter)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string baseKey = $"Event:{location.NameOrUniqueName}/{eventId}{suffix}";
            string uniqueKey = baseKey;
            int idx = 1;
            while (dict.ContainsKey(uniqueKey))
                uniqueKey = $"{baseKey}_{idx++}";

            dict[uniqueKey] = text;
            counter++;

            if (Config.developerModeOn)
                Monitor.Log($"[EV_GEN]         ADD {uniqueKey}  ←  \"{Trunc(text)}\"", LogLevel.Trace);
        }







        // Splits complex dialogue into separate lines, preserving event page breaks. Fixed issue with Events not splitting properly M and F lines
        private List<string> ExtractVoiceLinesFromDialogue(string rawText)
        {
            var results = new List<string>();
            if (string.IsNullOrWhiteSpace(rawText))
                return results;

            string s = rawText;

            // 1) If this is a $q branching block, keep only the spoken prompt text (before any '#$r' choices)
            int qIdx = s.IndexOf("$q", StringComparison.OrdinalIgnoreCase);
            if (qIdx >= 0)
            {
                int firstHash = s.IndexOf('#', qIdx);
                if (firstHash >= 0)
                {
                    string afterHeader = s.Substring(firstHash + 1); // prompt + rest
                    int nextChoice = afterHeader.IndexOf("#$r", StringComparison.Ordinal);
                    s = nextChoice >= 0 ? afterHeader.Substring(0, nextChoice) : afterHeader;
                }
                else
                {
                    // malformed $q: strip header if present
                    s = Regex.Replace(s, @"#?\$q\s*[^#]*#", "", RegexOptions.CultureInvariant);
                }
            }

            // 2) Drop any residual $r choice blocks if they remain (safety)
            s = Regex.Replace(
                s,
                @"#?\$r\s+\d+\s+-?\d+\s+\S+#.*?(?=(#?\$r\s+\d+\s+-?\d+\s+\S+#)|$)",
                "",
                RegexOptions.Singleline | RegexOptions.CultureInvariant
            );

            // 3) Normalize page-break markers to newlines
            s = Regex.Replace(
                s,
                @"#\s*\$b\s*#|#\s*\$b|\$b\s*#|\$b|#\s*#",
                "\n",
                RegexOptions.CultureInvariant
            );

            // 4) Strip mood/pause tokens and numeric pauses
            s = Regex.Replace(s, @"\$[a-zA-Z](\[[^\]]+\])?", "", RegexOptions.CultureInvariant);
            s = Regex.Replace(s, @"\$\d+", "", RegexOptions.CultureInvariant);

            // 5) Split into lines; if a line contains '^', emit both gender variants (same serial)
            foreach (var part in s.Split('\n'))
            {
                string line = part.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                int caret = line.IndexOf('^');
                if (caret >= 0)
                {
                    string male = line.Substring(0, caret).Trim();
                    string female = line.Substring(caret + 1).Trim();

                    if (!string.IsNullOrEmpty(male)) results.Add(male);    // will get :sN
                    if (!string.IsNullOrEmpty(female)) results.Add(female);  // will also get :sN (duplicate DialogueFrom => will dedupe with _1)
                }
                else
                {
                    results.Add(line);
                }
            }

            return results;
        }









        // Helper method to add and deduplicate dialogue

        private void AddEventDialogue( Dictionary<string, string> dict,GameLocation location,string eventId,string sanitizedText,ref int counter,string suffix = "",string genderTag = null // "male" | "female" | "nonbinary" | null
        )
        {
            if (string.IsNullOrWhiteSpace(sanitizedText))
                return;

            // Public TK stays "Event:<loc>/<id>:sN"
            string baseKey = $"Event:{location.NameOrUniqueName}/{eventId}{suffix}";

            // Internal storage key is unique per gender variant, so we don't create :sN_1
            string storageKey = genderTag is null ? baseKey : $"{baseKey}|g={genderTag}";

            dict[storageKey] = sanitizedText;
            counter++;
        }



}


}