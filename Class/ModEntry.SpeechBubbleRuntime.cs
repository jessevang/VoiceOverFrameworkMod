
using StardewValley;
using StardewValley.Characters;
using System.Runtime.CompilerServices;

namespace VoiceOverFrameworkMod
{
    public partial class ModEntry
    {
        private sealed class BubbleState
        {
            public string LastText = null;
            public int LastTimer = -1;
            public int StableTicks = 0;
            public bool Played = false;
        }

        private readonly ConditionalWeakTable<Character, BubbleState> _bubbleStates = new();

        private static IEnumerable<Character> EnumerateBubbleSpeakers()
        {
            if (Game1.currentLocation != null)
            {
                foreach (var c in Game1.currentLocation.characters)
                    yield return c;
            }

            if (Game1.player != null)
                yield return Game1.player;

            if (Game1.otherFarmers != null)
            {
                foreach (var f in Game1.otherFarmers.Values)
                    yield return f;
            }
        }

        private static Capture BuildBubbleCapture()
        {
            var cap = new Capture();
            var farmer = Game1.player;
            if (farmer != null)
            {
                cap.Add(
                    Utility.FilterUserName(farmer.Name),
                    Utility.FilterUserName(farmer.farmName.Value),
                    Utility.FilterUserName(farmer.favoriteThing.Value),
                    farmer.getPetDisplayName()
                );

                if (!string.IsNullOrWhiteSpace(farmer.spouse))
                    cap.Add(NPC.GetDisplayName(farmer.spouse));
                else
                {
                    var spouseId = farmer.team?.GetSpouse(farmer.UniqueMultiplayerID);
                    if (spouseId.HasValue)
                    {
                        var spouseFarmer = Game1.GetPlayer(spouseId.Value);
                        if (spouseFarmer != null) cap.Add(spouseFarmer.Name);
                    }
                }

                var kids = farmer.getChildren();
                if (kids.Count > 0) cap.Add(kids[0]?.displayName);
                if (kids.Count > 1) cap.Add(kids[1]?.displayName);
            }

            return cap;
        }

        private string SanitizeBubbleText(string displayed)
        {
            if (string.IsNullOrWhiteSpace(displayed))
                return string.Empty;

            // 1) Re-insert placeholders for the two common bubble args:
            //    {0} → farmer name, {1} → farm name
            var s = displayed;

            var farmer = Game1.player;
            if (farmer != null)
            {
                // Filter to match vanilla display form
                string farmerName = Utility.FilterUserName(farmer.Name) ?? "";
                string farmName = Utility.FilterUserName(farmer.farmName.Value) ?? "";

                if (!string.IsNullOrEmpty(farmerName))
                {
                    // Replace exact farmer name with {0}
                    s = System.Text.RegularExpressions.Regex.Replace(
                            s, System.Text.RegularExpressions.Regex.Escape(farmerName),
                            "{0}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }

                if (!string.IsNullOrEmpty(farmName))
                {
                    // Replace exact farm name with {1}
                    s = System.Text.RegularExpressions.Regex.Replace(
                            s, System.Text.RegularExpressions.Regex.Escape(farmName),
                            "{1}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
            }

            // 2) Strip the *rest* of the player/family names so they don’t block matching.
            var cap = BuildBubbleCapture(); // contains player/family words etc
            string stripped = DialogueSanitizerV2.StripChosenWords(s, cap);

            // 3) Canonicalize spacing/punctuation
            return CanonDisplay(stripped);
        }

    }
}
