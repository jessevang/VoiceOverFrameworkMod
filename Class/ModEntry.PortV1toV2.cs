// ModEntry.PortV1ToV2.cs — one-arg auto port: vof_port <V1 folder>
// - Generates V2 baseline templates automatically
// - Maps V1 -> V2 by DisplayPattern (with strict DP equality check)
// - Writes out V2 packs (same filenames) to "<V1>_output"
// - Writes per-character NeedsReview with ONLY V1 rows that failed to map
// - Copies audio from V1 -> V2; resolves conflicts with "-conflict-N" and updates JSON
//
// Added:
//  • Strict DP equality verification before attaching audio
//  • Report reuse of the same V1 filename across different DPs (suspicious)

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry : Mod
    {
        // ------------------------------------------------------------------------------------
        // Public command: vof_port <V1 folder>
        // (Add in SetupConsoleCommands: commands.Add("vof_port", "Auto-port a V1 voicepack folder to V2.", Cmd_PortAuto);)
        // -----------
        //
        // -------------------------------------------------------------------------

        private enum PortRosterFilter { All, Vanilla, Modded }
        private static bool TryParseRosterFilter(string s, out PortRosterFilter filter)
        {
            filter = PortRosterFilter.All;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "vanilla": filter = PortRosterFilter.Vanilla; return true;
                case "modded": filter = PortRosterFilter.Modded; return true;
                case "all": filter = PortRosterFilter.All; return true;
                default: return false;
            }
        }

        private static bool TryParseAudioFormat(string s, out string fmt)
        {
            fmt = null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim().ToLowerInvariant();
            if (t == "wav" || t == "ogg") { fmt = t; return true; }
            return false;
        }

        private bool MatchesRosterFilter(string character, PortRosterFilter filter)
        {
            switch (filter)
            {
                case PortRosterFilter.Vanilla: return IsKnownVanillaVillager(character);
                case PortRosterFilter.Modded: return !IsKnownVanillaVillager(character);
                case PortRosterFilter.All:
                default: return true;
            }
        }

        // ------------------------------------------------------------------------------------
        // Public command: vof_port <V1 folder> [vanilla|modded|all] [wav|ogg]
        // ------------------------------------------------------------------------------------
        private void Cmd_PortAuto(string cmd, string[] args)
        {
            if (args.Length < 1)
            {
                Monitor.Log("Usage: vof_port <V1 folder under Mods or absolute path> [vanilla|modded|all] [wav|ogg]", LogLevel.Info);
                Monitor.Log("Examples:", LogLevel.Info);
                Monitor.Log("  vof_port \"[VOFM] DM_Voicepack_En\"", LogLevel.Info);
                Monitor.Log("  vof_port \"[VOFM] DM_Voicepack_En\" vanilla ogg", LogLevel.Info);
                Monitor.Log("  vof_port \"[VOFM] DM_Voicepack_En\" modded", LogLevel.Info);
                return;
            }

            // Required
            string modsDir = Path.Combine(Constants.GamePath, "Mods");
            string v1Folder = V1V2_ResolvePathUnderMods(args[0], modsDir);

            // Optional (order-independent): roster filter + audio format
            PortRosterFilter rosterFilter = PortRosterFilter.All;
            string audioFormat = "ogg"; // default

            if (args.Length >= 2)
            {
                // args[1] may be filter or format
                if (TryParseRosterFilter(args[1], out var rf)) rosterFilter = rf;
                else if (TryParseAudioFormat(args[1], out var fmt1)) audioFormat = fmt1;
                else Monitor.Log($"Unknown option '{args[1]}'. Ignoring.", LogLevel.Warn);
            }
            if (args.Length >= 3)
            {
                // args[2] should be the remaining one (format or filter)
                if (TryParseRosterFilter(args[2], out var rf)) rosterFilter = rf;
                else if (TryParseAudioFormat(args[2], out var fmt2)) audioFormat = fmt2;
                else Monitor.Log($"Unknown option '{args[2]}'. Ignoring.", LogLevel.Warn);
            }

            string outFolder = V1V2_DefaultOutputFolder(v1Folder);
            string baselineFolder = Path.Combine(outFolder, "_baseline");

            Monitor.Log("===========================================", LogLevel.Info);
            Monitor.Log(" V1 → V2 port: starting…", LogLevel.Info);
            Monitor.Log($"  V1 input      : {v1Folder}", LogLevel.Info);
            Monitor.Log($"  Output        : {outFolder}", LogLevel.Info);
            Monitor.Log($"  Baseline (V2) : {baselineFolder}", LogLevel.Info);
            Monitor.Log($"  Audio Format  : {audioFormat}", LogLevel.Info);
            Monitor.Log($"  Roster Filter : {rosterFilter}", LogLevel.Info);
            Monitor.Log("===========================================", LogLevel.Info);

            var sw = Stopwatch.StartNew();

            if (!Directory.Exists(v1Folder))
            {
                Monitor.Log($"V1 folder not found: {v1Folder}", LogLevel.Error);
                return;
            }

            // Copy manifest.json if present
            Directory.CreateDirectory(outFolder);
            {
                string v1Manifest = Path.Combine(v1Folder, "manifest.json");
                if (File.Exists(v1Manifest))
                {
                    string outManifest = Path.Combine(outFolder, "manifest.json");
                    try
                    {
                        File.Copy(v1Manifest, outManifest, overwrite: true);
                        Monitor.Log("Copied manifest.json to output (unchanged).", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"Failed to copy manifest.json: {ex.Message}", LogLevel.Warn);
                    }
                }
                else
                {
                    Monitor.Log("No manifest.json found in V1 folder; skipping manifest copy.", LogLevel.Trace);
                }
            }

            var v1Files = Directory
                .EnumerateFiles(v1Folder, "*.json", SearchOption.TopDirectoryOnly)
                .Where(p => !Path.GetFileName(p).Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (v1Files.Count == 0)
            {
                Monitor.Log("No V1 JSON files found. Nothing to do.", LogLevel.Warn);
                return;
            }
            Monitor.Log($"Discovered {v1Files.Count} V1 JSON file(s).", LogLevel.Info);

            // Collect (Character, Language) pairs from V1, honoring roster filter
            var targets = new HashSet<(string Char, string Lang)>(new CharLangIgnoreCaseComparer());
            foreach (var fp in v1Files)
            {
                try
                {
                    var vf = JsonConvert.DeserializeObject<VoicePackFile>(File.ReadAllText(fp));
                    foreach (var vp in vf?.VoicePacks ?? Enumerable.Empty<VoicePackManifestTemplate>())
                    {
                        string c = vp.Character ?? "";
                        string l = vp.Language ?? "en";
                        if (!string.IsNullOrWhiteSpace(c) && MatchesRosterFilter(c, rosterFilter))
                            targets.Add((c, l));
                    }
                }
                catch
                {
                    // ignore malformed
                }
            }

            // If nothing matches after filtering, bail early
            if (targets.Count == 0)
            {
                Monitor.Log($"No character-language pairs matched the '{rosterFilter}' filter. Nothing to port.", LogLevel.Warn);
                return;
            }

            // Generate baseline templates (filtered)
            Directory.CreateDirectory(baselineFolder);
            Monitor.Log($"Generating baseline templates for {targets.Count} character-language pairs…", LogLevel.Info);
            foreach (var pair in targets)
            {
                string Char = pair.Char;
                string Lang = pair.Lang;
                string packId = $"AutoBaseline.{Char}.{Lang}";
                string packName = $"Auto Baseline ({Char} - {Lang})";
                try
                {
                    GenerateSingleTemplate(Char, Lang, baselineFolder, packId, packName, 1, audioFormat);
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Baseline gen failed for {Char} ({Lang}): {ex.Message}", LogLevel.Warn);
                }
            }

            string reportRoot = Path.Combine(outFolder, "Port_report");
            Directory.CreateDirectory(reportRoot);

            int filesWritten = 0;
            int grandAudioCopied = 0;

            // Port each V1 file
            for (int i = 0; i < v1Files.Count; i++)
            {
                string v1Path = v1Files[i];
                if (i == 0 || i % 10 == 0 || i == v1Files.Count - 1)
                    Monitor.Log($"Processing {i + 1}/{v1Files.Count}: {Path.GetFileName(v1Path)}", LogLevel.Info);

                try
                {
                    var vf = JsonConvert.DeserializeObject<VoicePackFile>(File.ReadAllText(v1Path));
                    if (vf?.VoicePacks == null || vf.VoicePacks.Count == 0)
                    {
                        V1V2_WritePerFileReport_NoPacks(reportRoot, v1Path);
                        continue;
                    }

                    var outPacks = new List<VoicePackManifestTemplate>();
                    var perPackReports = new Dictionary<string, V1V2_PackReport>(StringComparer.OrdinalIgnoreCase);

                    foreach (var v1pack in vf.VoicePacks)
                    {
                        string character = v1pack.Character ?? "";
                        string language = v1pack.Language ?? "en";
                        if (string.IsNullOrWhiteSpace(character))
                            continue;

                        // Skip packs that do not match roster filter
                        if (!MatchesRosterFilter(character, rosterFilter))
                            continue;

                        // Baseline path for this char/lang
                        string baselineFileName = $"{SanitizeKeyForFileName(character) ?? character}_{language}.json";
                        string baselinePath = Path.Combine(baselineFolder, baselineFileName);
                        if (!File.Exists(baselinePath))
                        {
                            V1V2_AppendReport_Skipped(perPackReports, v1Path, character, language, $"No baseline file named '{baselineFileName}' was found.");
                            continue;
                        }

                        var baselineVF = JsonConvert.DeserializeObject<VoicePackFile>(File.ReadAllText(baselinePath));
                        if (baselineVF?.VoicePacks == null || baselineVF.VoicePacks.Count == 0)
                        {
                            V1V2_AppendReport_Skipped(perPackReports, v1Path, character, language, "Baseline file had no VoicePacks.");
                            continue;
                        }

                        foreach (var basePack in baselineVF.VoicePacks)
                        {
                            if (!string.Equals(basePack.Character, character, StringComparison.OrdinalIgnoreCase) ||
                                !string.Equals(basePack.Language, language, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var outPack = new VoicePackManifestTemplate
                            {
                                Format = "2.0.0",
                                VoicePackId = string.IsNullOrWhiteSpace(v1pack.VoicePackId) ? basePack.VoicePackId : v1pack.VoicePackId,
                                VoicePackName = string.IsNullOrWhiteSpace(v1pack.VoicePackName) ? basePack.VoicePackName : v1pack.VoicePackName,
                                Character = basePack.Character,
                                Language = basePack.Language,
                                Entries = basePack.Entries.Select(e => new VoiceEntryTemplate
                                {
                                    DialogueFrom = e.DialogueFrom,
                                    DialogueText = e.DialogueText,
                                    AudioPath = e.AudioPath,
                                    TranslationKey = e.TranslationKey,
                                    PageIndex = e.PageIndex,
                                    DisplayPattern = e.DisplayPattern,
                                    GenderVariant = e.GenderVariant,
                                    BranchId = e.BranchId,
                                    DialogueTextPortedFromV1 = e.DialogueTextPortedFromV1
                                }).ToList()
                            };

                            // Build DP → indices
                            var dpToIndices = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                            for (int idx = 0; idx < outPack.Entries.Count; idx++)
                            {
                                string dp = outPack.Entries[idx].DisplayPattern ?? "";
                                if (string.IsNullOrWhiteSpace(dp)) continue;
                                if (!dpToIndices.TryGetValue(dp, out var list)) list = dpToIndices[dp] = new List<int>();
                                list.Add(idx);
                            }
                            var claimed = new HashSet<int>();
                            var pr = V1V2_GetOrCreatePackReport(perPackReports, character, language, v1Path);
                            var v1FileToDps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                            foreach (var v1e in v1pack.Entries ?? Enumerable.Empty<VoiceEntryTemplate>())
                            {
                                string v1Text = v1e?.DialogueText ?? "";
                                string v1Audio = v1e?.AudioPath ?? "";
                                if (string.IsNullOrWhiteSpace(v1Text) || string.IsNullOrWhiteSpace(v1Audio))
                                    continue;

                                pr.Summary.EntriesV1++;

                                string v1DP = V1V2_BuildDisplayPatternFromV1(v1Text);
                                string v1FileStem = Path.GetFileNameWithoutExtension(v1Audio) ?? v1Audio;
                                if (!v1FileToDps.TryGetValue(v1FileStem, out var set))
                                    v1FileToDps[v1FileStem] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                if (!string.IsNullOrWhiteSpace(v1DP))
                                    set.Add(v1DP);

                                if (string.IsNullOrWhiteSpace(v1DP) || !dpToIndices.TryGetValue(v1DP, out var indices) || indices.Count == 0)
                                {
                                    pr.Summary.NeedsReview++;
                                    pr.NeedsReview.Add(new V1V2_NeedsReviewRow
                                    {
                                        File = v1Path,
                                        Character = character,
                                        Language = language,
                                        DialogueTextV1 = v1Text,
                                        DisplayPatternV1 = v1DP,
                                        AudioPathV1 = v1Audio,
                                        Error = string.IsNullOrWhiteSpace(v1DP) ? "Empty/unsanitized DisplayPattern" : "No DP match in baseline"
                                    });
                                    continue;
                                }

                                int chosen = -1;
                                foreach (var idx in indices) { if (!claimed.Contains(idx)) { chosen = idx; break; } }
                                if (chosen < 0) chosen = indices[0];

                                var target = outPack.Entries[chosen];

                                if (!string.Equals(target.DisplayPattern ?? "", v1DP ?? "", StringComparison.OrdinalIgnoreCase))
                                {
                                    pr.Summary.NeedsReview++;
                                    pr.NeedsReview.Add(new V1V2_NeedsReviewRow
                                    {
                                        File = v1Path,
                                        Character = character,
                                        Language = language,
                                        DialogueTextV1 = v1Text,
                                        DisplayPatternV1 = v1DP,
                                        AudioPathV1 = v1Audio,
                                        Error = $"DP mismatch: baseline=\"{target.DisplayPattern ?? ""}\" vs v1=\"{v1DP}\""
                                    });
                                    continue;
                                }

                                string copiedRel = CopyOneMappedAudioNow(v1Folder, outFolder, language, character, v1Audio);
                                if (!string.IsNullOrWhiteSpace(copiedRel))
                                {
                                    target.AudioPath = copiedRel;
                                    target.DialogueTextPortedFromV1 = v1Text;
                                    claimed.Add(chosen);
                                    pr.Summary.Mapped++;
                                    grandAudioCopied++;
                                }
                                else
                                {
                                    pr.Summary.NeedsReview++;
                                    pr.NeedsReview.Add(new V1V2_NeedsReviewRow
                                    {
                                        File = v1Path,
                                        Character = character,
                                        Language = language,
                                        DialogueTextV1 = v1Text,
                                        DisplayPatternV1 = v1DP,
                                        AudioPathV1 = v1Audio,
                                        Error = "Audio source not found or failed to copy at mapping time"
                                    });
                                }
                            }

                            foreach (var kvp in v1FileToDps.Where(k => k.Value.Count > 1))
                            {
                                pr.Summary.NeedsReview++;
                                pr.NeedsReview.Add(new V1V2_NeedsReviewRow
                                {
                                    File = v1Path,
                                    Character = character,
                                    Language = language,
                                    DialogueTextV1 = string.Join(" || ", kvp.Value.Take(4)) + (kvp.Value.Count > 4 ? " ..." : ""),
                                    DisplayPatternV1 = "(multiple)",
                                    AudioPathV1 = kvp.Key,
                                    Error = "Same V1 file name reused for multiple different display patterns"
                                });
                            }

                            pr.Summary.EntriesV2 += outPack.Entries.Count;
                            outPacks.Add(outPack);
                        }
                    }

                    // Write filtered V2 file (if any packs remain)
                    string outPath = Path.Combine(outFolder, Path.GetFileName(v1Path));
                    var outObj = new VoicePackFile { Format = "2.0.0", VoicePacks = outPacks };

                    string jsonOut = JsonConvert.SerializeObject(outObj, Formatting.Indented,
                        new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    File.WriteAllText(outPath, jsonOut);
                    filesWritten++;

                    // Per-pack reports
                    foreach (var kv in perPackReports)
                    {
                        var pr = kv.Value;
                        if (pr.Summary.NeedsReview > 0 || pr.Summary.SkippedFiles > 0)
                        {
                            string stem = $"{V1V2_SafeName(pr.Character)}_{V1V2_SafeName(pr.Language)}";
                            string reviewPath = Path.Combine(reportRoot, $"{stem}.json");
                            File.WriteAllText(reviewPath, JsonConvert.SerializeObject(pr, Formatting.Indented));
                        }
                    }
                }
                catch (Exception ex)
                {
                    V1V2_WritePerFileReport_Error(reportRoot, v1Path, ex);
                }
            }

            sw.Stop();
            Monitor.Log("===========================================", LogLevel.Info);
            Monitor.Log(" V1 → V2 port: completed", LogLevel.Info);
            Monitor.Log($"  Files written : {filesWritten}", LogLevel.Info);
            if (grandAudioCopied > 0)
                Monitor.Log($"  Audio copied  : {grandAudioCopied}", LogLevel.Info);
            Monitor.Log($"  Report folder : {Path.Combine(outFolder, "Port_report")}", LogLevel.Info);
            Monitor.Log($"  Elapsed       : {sw.Elapsed:mm\\:ss}", LogLevel.Info);
            Monitor.Log("===========================================", LogLevel.Info);
        }




        // ------------------------------------------------------------------------------------
        // Helpers (unique names to avoid collisions)
        // ------------------------------------------------------------------------------------

        private sealed class CharLangIgnoreCaseComparer : IEqualityComparer<(string Char, string Lang)>
        {
            public bool Equals((string Char, string Lang) x, (string Char, string Lang) y) =>
                string.Equals(x.Char, y.Char, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Lang, y.Lang, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode((string Char, string Lang) obj)
            {
                int h1 = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Char ?? "");
                int h2 = StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Lang ?? "");
                return unchecked((h1 * 397) ^ h2);
            }
        }

        private static string V1V2_ResolvePathUnderMods(string pathArg, string modsDir)
        {
            if (string.IsNullOrWhiteSpace(pathArg)) return modsDir;
            if (Path.IsPathRooted(pathArg)) return Path.GetFullPath(pathArg);
            return Path.GetFullPath(Path.Combine(modsDir, pathArg));
        }

        private static string V1V2_DefaultOutputFolder(string v1Folder)
        {
            string parent = Path.GetDirectoryName(v1Folder) ?? v1Folder;
            string name = Path.GetFileName(v1Folder)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? "output";
            return Path.Combine(parent, name + "_output");
        }

        private static void V1V2_CopyAssetsIfPresent(string inputFolder, string outputFolder)
        {
            string src = Path.Combine(inputFolder, "assets");
            if (!Directory.Exists(src)) return;

            string dst = Path.Combine(outputFolder, "assets");
            foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, dir);
                Directory.CreateDirectory(Path.Combine(dst, rel));
            }
            foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(src, file);
                string outPath = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                if (!File.Exists(outPath))
                    File.Copy(file, outPath, overwrite: false);
            }
        }

        private static string V1V2_SafeName(string s) =>
            string.IsNullOrWhiteSpace(s) ? "(unknown)" : Regex.Replace(s, @"[^\w.-]+", "_");

        private void V1V2_WritePerFileReport_NoPacks(string reportRoot, string v1Path)
        {
            string stem = Path.GetFileNameWithoutExtension(v1Path);
            string path = Path.Combine(reportRoot, $"{stem}.json");
            var obj = new { File = v1Path, Summary = new { EntriesV1 = 0, EntriesV2 = 0, Mapped = 0, NeedsReview = 0 }, NeedsReview = Array.Empty<object>() };
            File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
        }

        private void V1V2_WritePerFileReport_Error(string reportRoot, string v1Path, Exception ex)
        {
            string stem = Path.GetFileNameWithoutExtension(v1Path);
            string path = Path.Combine(reportRoot, $"{stem}.json");
            var obj = new { File = v1Path, Error = ex.Message };
            File.WriteAllText(path, JsonConvert.SerializeObject(obj, Formatting.Indented));
        }

        private void V1V2_AppendReport_Skipped(Dictionary<string, V1V2_PackReport> map, string v1Path, string character, string language, string reason)
        {
            var pr = V1V2_GetOrCreatePackReport(map, character, language, v1Path);
            pr.Summary.SkippedFiles++;
            pr.NeedsReview.Add(new V1V2_NeedsReviewRow
            {
                File = v1Path,
                Character = character,
                Language = language,
                DialogueTextV1 = null,
                DisplayPatternV1 = null,
                AudioPathV1 = null,
                Error = reason
            });
        }

        // Normalize V1 text like runtime DisplayPattern (uses your DialogueUtil rules)
        private string V1V2_BuildDisplayPatternFromV1(string v1Text)
        {
            if (string.IsNullOrWhiteSpace(v1Text)) return string.Empty;

            //We want to skip spiltting as it'll break V1 text to multipleline with 1 audio making it incorrect when matching with V2.
            var pages = DialogueUtil.SplitAndSanitize(v1Text, splitBAsPage: false);  
            string display = pages.Count > 0 ? pages[0].Display : CanonDisplay(v1Text);
            return CanonDisplay(display);
        }

        // --- per-pack report model (only V1 rows in NeedsReview) ---
        private sealed class V1V2_PackReport
        {
            public string File { get; set; }
            public string Character { get; set; }
            public string Language { get; set; }
            public V1V2_Summary Summary { get; set; } = new();
            public List<V1V2_NeedsReviewRow> NeedsReview { get; set; } = new();
        }

        private sealed class V1V2_Summary
        {
            public int EntriesV1 { get; set; }
            public int EntriesV2 { get; set; }
            public int Mapped { get; set; }
            public int NeedsReview { get; set; }
            public int SkippedFiles { get; set; }
        }

        private sealed class V1V2_NeedsReviewRow
        {
            public string File { get; set; }
            public string Character { get; set; }
            public string Language { get; set; }

            public string? DialogueTextV1 { get; set; }
            public string? DisplayPatternV1 { get; set; }
            public string? AudioPathV1 { get; set; }

            public string? Error { get; set; }
        }

        private static V1V2_PackReport V1V2_GetOrCreatePackReport(
            Dictionary<string, V1V2_PackReport> map,
            string character,
            string language,
            string file)
        {
            string key = $"{character}||{language}";
            if (!map.TryGetValue(key, out var pr))
            {
                pr = new V1V2_PackReport
                {
                    File = file,
                    Character = character,
                    Language = language,
                    Summary = new V1V2_Summary()
                };
                map[key] = pr;
            }
            return pr;
        }

        private static string NormalizeSlashes(string p) => p?.Replace('\\', '/');

        private static string ComputeSha1(string path)
        {
            using var fs = File.OpenRead(path);
            using var sha1 = SHA1.Create();
            var hash = sha1.ComputeHash(fs);
            return BitConverter.ToString(hash).Replace("-", "");
        }

        /// <summary>
        /// Copy mapped audio from the V1 pack folder into the V2 output folder,
        /// renaming to Ported_<originalName> but **keeping the source file extension**.
        /// Updates the V2 entry's AudioPath to the new renamed file.
        /// Assumes the entry.AudioPath currently points to the V1 source path (set during mapping).
        /// </summary>
        private int CopyMappedAudioAndRename(
            string v1Root,            // e.g., "...\Mods\[VOFM] DM_Voicepack_En"
            string outRoot,           // e.g., "...\Mods\[VOFM] DM_Voicepack_En_output"
            VoicePackFile outFileObj  // the V2 file object being written
        )
        {
            int copied = 0;
            if (outFileObj?.VoicePacks == null) return 0;

            foreach (var pack in outFileObj.VoicePacks)
            {
                if (pack?.Entries == null) continue;

                string charSafe = SanitizeKeyForFileName(pack.Character) ?? pack.Character ?? "Unknown";
                string lang = string.IsNullOrWhiteSpace(pack.Language) ? "en" : pack.Language;

                foreach (var e in pack.Entries)
                {
                    if (string.IsNullOrWhiteSpace(e?.AudioPath)) continue;

                    // Resolve source (V1) file from whatever we staged into AudioPath during mapping.
                    string srcRel = NormalizeSlashes(e.AudioPath);
                    string srcFull = Path.IsPathRooted(srcRel) ? srcRel : Path.Combine(v1Root, srcRel);

                    if (!File.Exists(srcFull))
                    {
                        // Try a common fallback under assets/<lang>/<char>/
                        string tryAlt = Path.Combine(v1Root, "assets", lang, charSafe, Path.GetFileName(srcRel) ?? "");
                        if (File.Exists(tryAlt))
                            srcFull = tryAlt;
                        else
                            continue; // source missing → skip
                    }

                    string destDir = Path.Combine(outRoot, "assets", lang, charSafe);
                    Directory.CreateDirectory(destDir);

                    string origNoExt = Path.GetFileNameWithoutExtension(srcFull) ?? "audio";
                    string srcExt = Path.GetExtension(srcFull); // keep original extension exactly
                    string safeExt = string.IsNullOrWhiteSpace(srcExt) ? "" : srcExt.ToLowerInvariant();

                    string destName = $"Ported_{origNoExt}{safeExt}";
                    string destFull = Path.Combine(destDir, destName);

                    // ensure uniqueness (don’t overwrite different content)
                    int n = 1;
                    while (File.Exists(destFull))
                    {
                        destName = $"Ported_{origNoExt}_{n}{safeExt}";
                        destFull = Path.Combine(destDir, destName);
                        n++;
                    }

                    try
                    {
                        File.Copy(srcFull, destFull, overwrite: false);

                        // Update JSON path to the new Ported_* file
                        string destRel = $"assets/{lang}/{charSafe}/{destName}".Replace('\\', '/');
                        e.AudioPath = destRel;

                        copied++;
                    }
                    catch (Exception ex)
                    {
                        Monitor.Log($"Failed to copy audio {srcFull} → {destFull}: {ex.Message}", LogLevel.Warn);
                    }
                }
            }

            return copied;
        }

        /// <summary>
        /// Immediately copy ONE mapped audio from V1 → V2 and return the new relative path.
        /// If the suggested destination name already exists:
        ///  • If contents are identical (sha1), reuse it.
        ///  • If contents differ, suffix with _conflict-N.
        /// Returns null if source couldn't be found/copied.
        /// </summary>
        private string CopyOneMappedAudioNow(
            string v1Root,     // e.g. ...\Mods\[VOFM] DM_Voicepack_En
            string outRoot,    // e.g. ...\Mods\[VOFM] DM_Voicepack_En_output
            string language,   // e.g. "en"
            string character,  // e.g. "Abigail"
            string v1AudioRelOrAbs // as found in V1 entry (can be relative or absolute)
        )
        {
            if (string.IsNullOrWhiteSpace(v1AudioRelOrAbs))
                return null;

            // resolve source
            string charSafe = SanitizeKeyForFileName(character) ?? character ?? "Unknown";
            string srcRel = NormalizeSlashes(v1AudioRelOrAbs);
            string srcFull = Path.IsPathRooted(srcRel) ? srcRel : Path.Combine(v1Root, srcRel);

            if (!File.Exists(srcFull))
            {
                // try fallback under assets/<lang>/<char>/
                string tryAlt = Path.Combine(v1Root, "assets", language, charSafe, Path.GetFileName(srcRel) ?? "");
                if (File.Exists(tryAlt))
                    srcFull = tryAlt;
                else
                {
                    Monitor.Log($"Audio source missing: {srcRel}", LogLevel.Trace);
                    return null;
                }
            }

            // destination
            string destDir = Path.Combine(outRoot, "assets", language, charSafe);
            Directory.CreateDirectory(destDir);

            string origNoExt = Path.GetFileNameWithoutExtension(srcFull) ?? "audio";
            string srcExt = Path.GetExtension(srcFull);
            string safeExt = string.IsNullOrWhiteSpace(srcExt) ? "" : srcExt.ToLowerInvariant();

            string baseName = $"Ported_{origNoExt}";
            string destName = $"{baseName}{safeExt}";
            string destFull = Path.Combine(destDir, destName);

            // if exists, check content; else make conflict suffix
            if (File.Exists(destFull))
            {
                try
                {
                    string srcSha = ComputeSha1(srcFull);
                    string dstSha = ComputeSha1(destFull);
                    if (!string.Equals(srcSha, dstSha, StringComparison.OrdinalIgnoreCase))
                    {
                        int n = 1;
                        do
                        {
                            destName = $"{baseName}_conflict-{n}{safeExt}";
                            destFull = Path.Combine(destDir, destName);
                            n++;
                        } while (File.Exists(destFull));
                    }
                    else
                    {
                        // identical → reuse
                        string destRelExisting = $"assets/{language}/{charSafe}/{Path.GetFileName(destFull)}".Replace('\\', '/');
                        return destRelExisting;
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"SHA check failed, will attempt unique copy: {ex.Message}", LogLevel.Trace);
                    int n = 1;
                    do
                    {
                        destName = $"{baseName}_conflict-{n}{safeExt}";
                        destFull = Path.Combine(destDir, destName);
                        n++;
                    } while (File.Exists(destFull));
                }
            }

            try
            {
                File.Copy(srcFull, destFull, overwrite: false);
                string destRel = $"assets/{language}/{charSafe}/{destName}".Replace('\\', '/');
                return destRel;
            }
            catch (Exception ex)
            {
                Monitor.Log($"Failed to copy audio {srcFull} → {destFull}: {ex.Message}", LogLevel.Warn);
                return null;
            }
        }








    }


}
