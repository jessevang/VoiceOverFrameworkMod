using StardewValley;
using StardewValley.Menus;
using StardewValley.BellsAndWhistles;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley.Objects; // Needed for Chest, potentially other items if used later
using StardewValley.Characters; // Needed for SchedulePathDescription if that constructor was used

namespace VoiceOverFrameworkMod.Menus
{
    public class VoiceTestMenu : IClickableMenu
    {
        private enum MenuState { CharacterList, DialogueList }
        private MenuState CurrentState = MenuState.CharacterList;

        private readonly Dictionary<string, List<VoicePack>> VoicePacks;
        private List<string> CharacterNames;
        private string SelectedCharacter;

        private List<(string key, string audioPath)> DialogueEntries;
        private List<(string key, string audioPath)> FilteredDialogueEntries;

        private int CharacterScrollOffset = 0;
        private int DialogueScrollOffset = 0;

        private string SearchQuery = "";
        private TextBox SearchBox;
        private ClickableTextureComponent BackButton;
        // private ClickableTextureComponent SearchButton; // Not using a visual search button

        // --- Dynamic Layout Variables ---
        private int _menuWidth;
        private int _menuHeight;
        private int _menuX;
        private int _menuY;

        private Rectangle _characterListArea;
        private Rectangle _dialogueListArea;
        private int _listItemHeight;
        private int _maxVisibleCharacters;
        private int _maxVisibleDialogues;

        private Rectangle _titleBounds;
        private Rectangle _characterTitleBounds;
        // private Rectangle _dialogueTitleBounds; // Title bounds are dynamic based on text now
        private Rectangle _searchLabelBounds;
        private Rectangle _searchBoxBounds;
        private Rectangle _backButtonBounds;
        // --- End Dynamic Layout Variables ---


        public VoiceTestMenu(Dictionary<string, List<VoicePack>> packs)
            : base(0, 0, 0, 0, true) // Initialize with dummy values, will be set in UpdateLayout
        {
            VoicePacks = packs;
            CharacterNames = packs.Keys.OrderBy(name => name).ToList();

            // Initialize components (positions/sizes will be set in UpdateLayout)
            SearchBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.dialogueFont, Game1.textColor)
            {
                X = 0, // Placeholder
                Y = 0, // Placeholder
                Width = 100, // Placeholder
                Selected = false // Start deselected
            };

            BackButton = new ClickableTextureComponent("Back", Rectangle.Empty, null, "Back", Game1.mouseCursors, new Rectangle(352, 495, 12, 11), 1f); // Scale will be set

            // Initial layout calculation
            UpdateLayout();
        }

        /// <summary>
        /// Recalculates the position and size of the menu and all its components
        /// based on the current screen size and UI scale.
        /// </summary>
        private void UpdateLayout()
        {
            // --- Menu Bounds ---
            float scale = 0.8f;
            _menuWidth = (int)(Game1.uiViewport.Width * scale);
            _menuHeight = (int)(Game1.uiViewport.Height * scale);
            _menuX = (Game1.uiViewport.Width - _menuWidth) / 2;
            _menuY = (Game1.uiViewport.Height - _menuHeight) / 2;

            // Update base class properties
            xPositionOnScreen = _menuX;
            yPositionOnScreen = _menuY;
            width = _menuWidth;
            height = _menuHeight;

            // Initialize the upper-right close button (standard menu feature)
            initializeUpperRightCloseButton(); // This uses the updated x, y, width, height

            // --- Common Measurements ---
            int margin = Game1.tileSize / 2;
            int padding = Game1.tileSize / 4;
            int buttonSize = Game1.tileSize;
            // Use dialogueFont for height consistency if dialogue keys use it
            _listItemHeight = (int)(Game1.dialogueFont.MeasureString("Tq").Y * 1.5f);


            // --- Back Button (Only visible in DialogueList state, but calculate always for simplicity) ---
            _backButtonBounds = new Rectangle(
                _menuX + margin,
                _menuY + margin,
                buttonSize,
                buttonSize
            );
            BackButton.bounds = _backButtonBounds;
            BackButton.scale = (float)buttonSize / BackButton.sourceRect.Width; // Adjust scale to fit bounds

            // --- Title (Position calculated dynamically based on text in Draw) ---
            // We calculate the Y position roughly here to help layout other elements.
            int titleY = _menuY + margin + padding; // Placeholder Y for layout calculations below

            // --- Character List State ---
            if (CurrentState == MenuState.CharacterList)
            {
                string charTitleText = "Characters";
                Vector2 charTitleSize = Game1.smallFont.MeasureString(charTitleText);
                _characterTitleBounds = new Rectangle(
                   _menuX + margin,
                   titleY + (int)(Game1.dialogueFont.MeasureString("Tq").Y) + margin, // Below main title area
                   (int)charTitleSize.X,
                   (int)charTitleSize.Y
                );

                _characterListArea = new Rectangle(
                    _menuX + margin,
                    _characterTitleBounds.Bottom + padding,
                    _menuWidth - margin * 2,
                    _menuHeight - (_characterTitleBounds.Bottom + padding - _menuY) - margin // Remaining height
                );

                _maxVisibleCharacters = Math.Max(1, _characterListArea.Height / _listItemHeight);
                // Clamp scroll offset
                CharacterScrollOffset = Math.Min(CharacterScrollOffset, Math.Max(0, CharacterNames.Count - _maxVisibleCharacters));
            }
            // --- Dialogue List State ---
            else if (CurrentState == MenuState.DialogueList)
            {
                // Note: Title area is defined by _titleBounds calculated in Draw

                // Search Label
                string searchLabelText = "Search:";
                Vector2 searchLabelSize = Game1.smallFont.MeasureString(searchLabelText);
                _searchLabelBounds = new Rectangle(
                   // Position below back button AND potential title area
                   _menuX + margin,
                    _backButtonBounds.Bottom + padding,
                   (int)searchLabelSize.X,
                   (int)searchLabelSize.Y
                );

                // Search Box
                int searchBoxHeight = Game1.tileSize;
                _searchBoxBounds = new Rectangle(
                    _searchLabelBounds.Right + padding,
                    _searchLabelBounds.Y - (searchBoxHeight - _searchLabelBounds.Height) / 2, // Align center vertically with label
                     _menuWidth - (_searchLabelBounds.Right + padding - _menuX) - margin, // Remaining width
                    searchBoxHeight
                );
                SearchBox.X = _searchBoxBounds.X;
                SearchBox.Y = _searchBoxBounds.Y;
                SearchBox.Width = _searchBoxBounds.Width;


                _dialogueListArea = new Rectangle(
                   _menuX + margin,
                   _searchBoxBounds.Bottom + margin, // Below search area
                   _menuWidth - margin * 2,
                   _menuHeight - (_searchBoxBounds.Bottom + margin - _menuY) - margin // Remaining height
                );

                _maxVisibleDialogues = Math.Max(1, _dialogueListArea.Height / _listItemHeight);
                // Clamp scroll offset
                if (FilteredDialogueEntries != null)
                {
                    DialogueScrollOffset = Math.Min(DialogueScrollOffset, Math.Max(0, FilteredDialogueEntries.Count - _maxVisibleDialogues));
                }
                else
                {
                    DialogueScrollOffset = 0;
                }
            }
        }

        // Make sure the keyboard subscriber is released when the menu is closed.
        public override void emergencyShutDown()
        {
            base.emergencyShutDown();
            if (Game1.keyboardDispatcher.Subscriber == SearchBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            UpdateLayout();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (CurrentState == MenuState.CharacterList && CharacterNames.Count > _maxVisibleCharacters)
            {
                int scrollAmount = direction / -120; // Invert direction for natural scrolling
                int maxScroll = CharacterNames.Count - _maxVisibleCharacters;
                CharacterScrollOffset = Math.Max(0, Math.Min(CharacterScrollOffset + scrollAmount, maxScroll));
                if (scrollAmount != 0) Game1.playSound("shiny4");
            }
            else if (CurrentState == MenuState.DialogueList && FilteredDialogueEntries != null && FilteredDialogueEntries.Count > _maxVisibleDialogues)
            {
                int scrollAmount = direction / -120; // Invert direction
                int maxScroll = FilteredDialogueEntries.Count - _maxVisibleDialogues;
                DialogueScrollOffset = Math.Max(0, Math.Min(DialogueScrollOffset + scrollAmount, maxScroll));
                if (scrollAmount != 0) Game1.playSound("shiny4");
            }
        }

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);

            if (CurrentState == MenuState.DialogueList)
            {
                BackButton.tryHover(x, y, 0.25f);
            }
            // Hover for list items is handled visually in draw()
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound); // Handle close button first

            

            if (CurrentState == MenuState.DialogueList)
            {
                if (BackButton.containsPoint(x, y))
                {
                    Game1.playSound("bigDeSelect");
                    CurrentState = MenuState.CharacterList;
                    SelectedCharacter = null;
                    DialogueEntries = null;
                    FilteredDialogueEntries = null;
                    SearchQuery = "";
                    SearchBox.Text = "";
                    if (Game1.keyboardDispatcher.Subscriber == SearchBox)
                        Game1.keyboardDispatcher.Subscriber = null;
                    SearchBox.Selected = false;
                    CharacterScrollOffset = 0; // Reset character scroll too
                    UpdateLayout();
                    return;
                }

                if (_searchBoxBounds.Contains(x, y))
                {
                    if (!SearchBox.Selected)
                    {
                        SearchBox.Selected = true;
                        Game1.keyboardDispatcher.Subscriber = SearchBox;
                    }
                }
                else
                {
                    if (SearchBox.Selected)
                    {
                        SearchBox.Selected = false;
                        if (Game1.keyboardDispatcher.Subscriber == SearchBox)
                            Game1.keyboardDispatcher.Subscriber = null;
                    }
                }

                if (FilteredDialogueEntries != null && _dialogueListArea.Contains(x, y))
                {
                    int startIndex = DialogueScrollOffset;
                    int count = Math.Min(_maxVisibleDialogues, FilteredDialogueEntries.Count - startIndex);

                    for (int i = 0; i < count; i++)
                    {
                        int actualIndex = startIndex + i;
                        int itemY = _dialogueListArea.Y + i * _listItemHeight;
                        Rectangle itemRect = new Rectangle(
                            _dialogueListArea.X, itemY, _dialogueListArea.Width, _listItemHeight
                        );

                        if (itemRect.Contains(x, y))
                        {
                            var entry = FilteredDialogueEntries[actualIndex];
                            TriggerDialogue(SelectedCharacter, entry.key);
                            Game1.playSound("selectItem");
                            break;
                        }
                    }
                }
            }
            else if (CurrentState == MenuState.CharacterList)
            {
                if (_characterListArea.Contains(x, y))
                {
                    int startIndex = CharacterScrollOffset;
                    int count = Math.Min(_maxVisibleCharacters, CharacterNames.Count - startIndex);

                    for (int i = 0; i < count; i++)
                    {
                        int actualIndex = startIndex + i;
                        int itemY = _characterListArea.Y + i * _listItemHeight;
                        Rectangle itemRect = new Rectangle(
                           _characterListArea.X, itemY, _characterListArea.Width, _listItemHeight
                        );

                        if (itemRect.Contains(x, y))
                        {
                            SelectedCharacter = CharacterNames[actualIndex];
                            DialogueEntries = VoicePacks[SelectedCharacter]
                                .SelectMany(p => p.Entries.Select(e => (e.Key, e.Value)))
                                .Distinct()
                                .OrderBy(e => e.Key)
                                .ToList();

                            ApplySearchFilter();
                            DialogueScrollOffset = 0;
                            SearchQuery = "";
                            SearchBox.Text = "";
                            SearchBox.Selected = false;
                            if (Game1.keyboardDispatcher.Subscriber == SearchBox)
                                Game1.keyboardDispatcher.Subscriber = null;


                            ModEntry.Instance.Monitor.Log($"Selected {SelectedCharacter} with {DialogueEntries.Count} dialogues", LogLevel.Info);
                            CurrentState = MenuState.DialogueList;
                            UpdateLayout();
                            Game1.playSound("newArtifact");
                            break;
                        }
                    }
                }
            }
        }

        private void TriggerDialogue(string character, string dialogueKey)
        {
            var npc = Game1.getCharacterFromName(character);
            if (npc == null)
            {
                ModEntry.Instance.Monitor.Log($"NPC '{character}' not found in current location. Attempting to create temporary instance.", LogLevel.Trace);
                try
                {
                    // **FIX 1: Use a known NPC constructor**
                    // This constructor (sprite, position, defaultMap, facingDir, name, schedule(null), portrait) should work.
                    Texture2D portrait = null;
                    try
                    { // Defensive portrait loading
                        portrait = Game1.content.Load<Texture2D>($"Portraits\\{character}");
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Instance.Monitor.Log($"Could not load portrait for temporary NPC '{character}': {ex.Message}", LogLevel.Warn);
                    }

                    // Make sure the sprite path is valid or handle error
                    AnimatedSprite sprite = null;
                    try
                    {
                        sprite = new AnimatedSprite($"Characters\\{character}", 0, 16, 32);
                    }
                    catch (Exception ex)
                    {
                        ModEntry.Instance.Monitor.Log($"Could not load sprite for temporary NPC '{character}': {ex.Message}", LogLevel.Error);
                        Game1.drawObjectDialogue($"Error: Cannot load character sprite for '{character}'.");
                        return;
                    }


                    // public NPC(AnimatedSprite sprite, Vector2 position, string defaultMap, int facingDirection, string name, bool datable, Texture2D portrait)

                    npc = new NPC(
                       sprite,
                       new Vector2(-9999, -9999), // Position off-screen
                       "Town", // Default map (doesn't really matter here)
                       0, // Facing direction
                       character, // Name
                       false, 
                       portrait // Portrait texture
                                // eventActor defaults to false
                   );
                    ModEntry.Instance.Monitor.Log($"Created temporary NPC instance for '{character}'.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    ModEntry.Instance.Monitor.Log($"Failed to create temporary NPC instance for '{character}': {ex}", LogLevel.Error);
                    Game1.drawObjectDialogue($"Error: Could not create NPC '{character}' for test.");
                    return;
                }
            }

            // Ensure voice pack data exists
            if (!VoicePacks.ContainsKey(character) || !VoicePacks[character].Any(pack => pack.Entries.ContainsKey(dialogueKey)))
            {
                ModEntry.Instance.Monitor.Log($"Dialogue key '{dialogueKey}' not found in voice packs for {character}.", LogLevel.Warn);
                Game1.drawObjectDialogue($"Error: Voice for '{dialogueKey}' not found.");
                return;
            }


            string dummyText = $"[VOICE TEST] Playing: {dialogueKey}";
            // Using the key for lookup AND for display text now, as original text isn't stored here.
            Dialogue dialogue = new Dialogue(npc, dialogueKey, dummyText);

            npc.CurrentDialogue.Clear();
            npc.CurrentDialogue.Push(dialogue);
            Game1.currentSpeaker = npc;
            Game1.dialogueUp = true;
            Game1.player.CanMove = false;

            ModEntry.Instance.TryPlayVoice(character, dialogueKey);

            exitThisMenu(false);
        }


        private void ApplySearchFilter()
        {
            if (DialogueEntries == null)
            {
                FilteredDialogueEntries = new List<(string key, string audioPath)>();
                return;
            }

            if (string.IsNullOrWhiteSpace(SearchQuery))
            {
                FilteredDialogueEntries = DialogueEntries;
            }
            else
            {
                FilteredDialogueEntries = DialogueEntries
                    .Where(e => e.key.IndexOf(SearchQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
            DialogueScrollOffset = 0; // Reset scroll on filter change
            // No need to call UpdateLayout here unless scrollbars are visually added/removed
        }

        public override void update(GameTime time)
        {
            base.update(time);

            if (CurrentState == MenuState.DialogueList)
            {
                // Let TextBox handle its own update via Game1.keyboardDispatcher when selected
                if (SearchBox.Selected && Game1.keyboardDispatcher.Subscriber == SearchBox)
                {
                    if (SearchBox.Text != SearchQuery)
                    {
                        SearchQuery = SearchBox.Text;
                        ApplySearchFilter();
                    }
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            // Allow ESC/E to close unless textbox is active (and E is pressed)
            bool isExitKey = key == Keys.Escape || (Game1.options.doesInputListContain(Game1.options.menuButton, key) && !(SearchBox.Selected && Game1.keyboardDispatcher.Subscriber == SearchBox));

            if (isExitKey && readyToClose())
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu(true);
                return;
            }

            // Let the textbox handle input if it's selected
            if (SearchBox.Selected && Game1.keyboardDispatcher.Subscriber == SearchBox)
            {
                // TextBox handles typing via the dispatcher.
                // We prevent base.receiveKeyPress here so keys don't trigger game actions while typing.
                return;
            }

            // Handle scrolling with keyboard arrows maybe? (Optional)
            // if (key == Keys.Up) receiveScrollWheelAction(120);
            // else if (key == Keys.Down) receiveScrollWheelAction(-120);


            // Otherwise, pass to base for other potential handling
            base.receiveKeyPress(key);
        }

        // **FIX 2 & 3: Helper method to truncate string**
        /// <summary>Truncates a string with an ellipsis if its rendered width exceeds a maximum.</summary>
        /// <param name="text">The string to truncate.</param>
        /// <param name="maxWidth">The maximum allowed pixel width.</param>
        /// <param name="font">The font used for rendering.</param>
        /// <returns>The original string, or the truncated string with an ellipsis.</returns>
        private string TruncateString(string text, float maxWidth, SpriteFont font)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0 || font == null) return "";

            Vector2 size = font.MeasureString(text);
            if (size.X <= maxWidth)
            {
                return text; // It fits
            }

            // If it doesn't fit, prepare ellipsis
            string ellipsis = "...";
            Vector2 ellipsisSize = font.MeasureString(ellipsis);

            // Reduce max width by ellipsis width
            maxWidth -= ellipsisSize.X;
            if (maxWidth <= 0) return ellipsis; // Not enough space even for ellipsis

            string currentText = text;
            while (currentText.Length > 0)
            {
                string trial = currentText.Substring(0, currentText.Length - 1);
                if (font.MeasureString(trial).X <= maxWidth)
                {
                    // Found the longest substring that fits before the ellipsis
                    return trial + ellipsis;
                }
                currentText = trial; // Remove last character and try again
            }

            // If even the first character + ellipsis is too long, just return ellipsis
            return ellipsis;
        }


        public override void draw(SpriteBatch b)
        {
            // Ensure layout is up-to-date
            UpdateLayout();

            // Dim background
            if (!Game1.options.showClearBackgrounds)
                b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.6f);

            // Menu background box
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(384, 373, 18, 18),
                                           _menuX, _menuY, _menuWidth, _menuHeight,
                                           Color.White, Game1.pixelZoom, true);

            // Title - Calculate position dynamically based on text width for centering
            string titleText = CurrentState == MenuState.CharacterList ? "Voice Test Menu" : $"{SelectedCharacter}'s Dialogues";
            Vector2 titleSize = Game1.dialogueFont.MeasureString(titleText);
            int titleX = _menuX + (_menuWidth - (int)titleSize.X) / 2;
            int titleY = _menuY + Game1.tileSize / 2 + Game1.tileSize / 4; // Matches old _titleBounds Y calculation
                                                                           // Use the Truncate helper - title usually shouldn't need it, but for safety
            string truncatedTitle = TruncateString(titleText, _menuWidth - Game1.tileSize, Game1.dialogueFont);
            Utility.drawTextWithShadow(b, truncatedTitle, Game1.dialogueFont, new Vector2(titleX, titleY), Game1.textColor);


            if (CurrentState == MenuState.CharacterList)
            {
                Utility.drawTextWithShadow(b, "Characters", Game1.smallFont, new Vector2(_characterTitleBounds.X, _characterTitleBounds.Y), Game1.textColor);

                int startIndex = CharacterScrollOffset;
                int count = Math.Min(_maxVisibleCharacters, CharacterNames.Count - startIndex);

                for (int i = 0; i < count; i++)
                {
                    int actualIndex = startIndex + i;
                    string name = CharacterNames[actualIndex];
                    int itemY = _characterListArea.Y + i * _listItemHeight;
                    Rectangle itemRect = new Rectangle(_characterListArea.X, itemY, _characterListArea.Width, _listItemHeight);

                    if (itemRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                        b.Draw(Game1.staminaRect, itemRect, Color.Wheat * 0.5f);

                    Vector2 nameSize = Game1.dialogueFont.MeasureString(name);
                    float textY = itemRect.Y + (itemRect.Height - nameSize.Y) / 2;
                    // No truncation needed usually for character names, but use helper just in case
                    string truncatedName = TruncateString(name, itemRect.Width - Game1.tileSize / 4, Game1.dialogueFont);
                    Utility.drawTextWithShadow(b, truncatedName, Game1.dialogueFont, new Vector2(itemRect.X + Game1.tileSize / 4, textY), Game1.textColor);
                }
                // TODO: Draw Scrollbar for Characters
            }
            else if (CurrentState == MenuState.DialogueList)
            {
                BackButton.draw(b); // Draw back button using its bounds/scale set in UpdateLayout

                Utility.drawTextWithShadow(b, "Search:", Game1.smallFont, new Vector2(_searchLabelBounds.X, _searchLabelBounds.Y), Game1.textColor);
                SearchBox.Draw(b); // TextBox draws itself

                if (FilteredDialogueEntries != null)
                {
                    int startIndex = DialogueScrollOffset;
                    int count = Math.Min(_maxVisibleDialogues, FilteredDialogueEntries.Count - startIndex);

                    for (int i = 0; i < count; i++)
                    {
                        int actualIndex = startIndex + i;
                        var entry = FilteredDialogueEntries[actualIndex];
                        int itemY = _dialogueListArea.Y + i * _listItemHeight;
                        Rectangle itemRect = new Rectangle(_dialogueListArea.X, itemY, _dialogueListArea.Width, _listItemHeight);

                        if (itemRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                            b.Draw(Game1.staminaRect, itemRect, Color.Wheat * 0.5f);

                        // **FIX 3: Use TruncateString helper here**
                        string truncatedKey = TruncateString(entry.key, itemRect.Width - Game1.tileSize / 2, Game1.dialogueFont);
                        Vector2 keySize = Game1.dialogueFont.MeasureString(truncatedKey); // Measure truncated version for centering
                        float textY = itemRect.Y + (itemRect.Height - keySize.Y) / 2;
                        Utility.drawTextWithShadow(b, truncatedKey, Game1.dialogueFont, new Vector2(itemRect.X + Game1.tileSize / 4, textY), Game1.textColor);
                    }
                    // TODO: Draw Scrollbar for Dialogues
                }
            }

            // Draw close button (handled by base) and mouse
            base.draw(b);
            drawMouse(b);
        }
    }
}