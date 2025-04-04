// VoiceOverFrameworkMod.cs
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using Microsoft.Xna.Framework;

using Microsoft.Xna.Framework.Graphics;
using StardewValley.BellsAndWhistles;

namespace VoiceOverFrameworkMod
{
    public class ModEntry : Mod
    {
        private ModConfig Config;
        private Dictionary<string, string> SelectedVoicePacks;
        private Dictionary<string, List<VoicePack>> VoicePacksByCharacter = new();

        private string lastDialogueText = null;
        private string lastSpeakerName = null;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();
            SelectedVoicePacks = Config.SelectedVoicePacks;

            LoadVoicePacks();
            Monitor.Log($"VoicePacks loaded for: {string.Join(", ", VoicePacksByCharacter.Keys)}", LogLevel.Info);

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.Input.ButtonPressed += OnButtonPressed;
        }



        private void LoadVoicePacks()
        {
            var allContentPacks = this.Helper.ContentPacks.GetOwned();
            foreach (var pack in allContentPacks)
            {
                var manifestPath = Path.Combine(pack.DirectoryPath, "content.json");
                if (!File.Exists(manifestPath))
                    continue;

                var metadata = pack.ReadJsonFile<VoicePackManifest>("content.json");
                if (metadata == null || metadata.Entries == null)
                    continue;

                var voicePack = new VoicePack
                {
                    VoicePackId = metadata.VoicePackId,
                    Language = metadata.Language,
                    Character = metadata.Character,
                    Entries = metadata.Entries.ToDictionary(e => e.DialogueKey, e => Path.Combine(pack.DirectoryPath, e.AudioPath))
                };

                if (!VoicePacksByCharacter.ContainsKey(voicePack.Character))
                    VoicePacksByCharacter[voicePack.Character] = new();

                VoicePacksByCharacter[voicePack.Character].Add(voicePack);
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.currentSpeaker == null || !Game1.dialogueUp)
            {
                lastDialogueText = null;
                lastSpeakerName = null;
                return;
            }

            string speakerName = Game1.currentSpeaker.Name;
            string currentText = Game1.currentSpeaker?.CurrentDialogue?.FirstOrDefault()?.getCurrentDialogue()?.Trim();

            if (string.IsNullOrEmpty(currentText) || speakerName == null)
                return;

            if (currentText == lastDialogueText && speakerName == lastSpeakerName)
                return;

            lastDialogueText = currentText;
            lastSpeakerName = speakerName;

            TryPlayVoice(speakerName, currentText);
        }

        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            if (e.Button == SButton.F12)
            {
                Monitor.Log("F12 pressed — opening VoiceTestMenu", LogLevel.Debug);
                Game1.activeClickableMenu = new VoiceTestMenu(VoicePacksByCharacter);
            }
        }


        private void TryPlayVoice(string characterName, string dialogueKey)
        {
            string language = Config.DefaultLanguage;

            if (!SelectedVoicePacks.TryGetValue(characterName, out string selectedVoicePackId))
                return;

            if (!VoicePacksByCharacter.TryGetValue(characterName, out var voicePacks))
                return;

            var selectedPack = voicePacks.FirstOrDefault(p => p.VoicePackId == selectedVoicePackId && p.Language == language);
            if (selectedPack == null)
                return;

            if (selectedPack.Entries.TryGetValue(dialogueKey, out string audioPath) && File.Exists(audioPath))
            {
                Game1.playSound(audioPath); // Replace with actual audio playback
            }
        }
    }

    public class ModConfig
    {
        public string DefaultLanguage { get; set; } = "en";
        public bool FallbackToDefaultIfMissing { get; set; } = true;
        public Dictionary<string, string> SelectedVoicePacks { get; set; } = new();
    }

    public class VoicePackManifest
    {
        public string Format { get; set; }
        public string VoicePackId { get; set; }
        public string Character { get; set; }
        public string Language { get; set; }
        public List<VoiceEntry> Entries { get; set; }
    }

    public class VoiceEntry
    {
        public string DialogueKey { get; set; }
        public string AudioPath { get; set; }
    }

    public class VoicePack
    {
        public string VoicePackId;
        public string Character;
        public string Language;
        public Dictionary<string, string> Entries;
    }

    public class VoiceTestMenu : IClickableMenu
    {
        private readonly Dictionary<string, List<VoicePack>> VoicePacks;
        private List<string> CharacterNames;
        private string SelectedCharacter;
        private List<string> DialogueKeys;

        public VoiceTestMenu(Dictionary<string, List<VoicePack>> packs)
        {
            VoicePacks = packs;
            CharacterNames = packs.Keys.OrderBy(name => name).ToList();
            width = 600;
            height = 400;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
        }

        public override void draw(SpriteBatch b)
        {
            IClickableMenu.drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);
            SpriteText.drawString(b, "Select a Character:", xPositionOnScreen + 64, yPositionOnScreen + 32);

            for (int i = 0; i < CharacterNames.Count; i++)
            {
                string name = CharacterNames[i];
                Vector2 textSize = Game1.smallFont.MeasureString(name);
                Rectangle rect = new Rectangle(xPositionOnScreen + 64, yPositionOnScreen + 64 + (i * 32), (int)textSize.X, (int)textSize.Y);

                if (rect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                {
                    b.Draw(Game1.staminaRect, rect, Color.Gray);
                    if (Game1.input.GetMouseState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed)
                    {
                        SelectedCharacter = name;
                        DialogueKeys = VoicePacks[name].SelectMany(p => p.Entries.Keys).Distinct().OrderBy(k => k).ToList();
                    }
                }
                b.DrawString(Game1.smallFont, name, new Vector2(rect.X, rect.Y), Color.White);
            }

            if (!string.IsNullOrEmpty(SelectedCharacter) && DialogueKeys != null)
            {
                SpriteText.drawString(b, $"{SelectedCharacter}'s Dialogues:", xPositionOnScreen + 300, yPositionOnScreen + 32);
                for (int i = 0; i < DialogueKeys.Count && i < 10; i++)
                {
                    string key = DialogueKeys[i];
                    Vector2 textSize = Game1.smallFont.MeasureString(key);
                    Rectangle rect = new Rectangle(xPositionOnScreen + 300, yPositionOnScreen + 64 + (i * 32), (int)textSize.X, (int)textSize.Y);

                    if (rect.Contains(Game1.getMouseX(), Game1.getMouseY()) && Game1.input.GetMouseState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed)
                    {
                        NPC npc = Game1.getCharacterFromName(SelectedCharacter);
                        if (npc != null)
                        {
                            foreach (var pack in VoicePacks[SelectedCharacter])
                            {
                                if (pack.Entries.TryGetValue(key, out string text))
                                {
                                    Game1.drawDialogue(npc); // This shows the dialogue key — update to real text if needed
                                    Game1.exitActiveMenu();
                                    break;
                                }
                            }
                        }
                    }
                    b.DrawString(Game1.smallFont, key, new Vector2(rect.X, rect.Y), Color.Yellow);
                }
            }

            base.drawMouse(b);
        }
    }
}