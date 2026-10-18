using System.Collections.Generic;

namespace DevMenuUnlock
{
    /// <summary>Czech labels and icons for the game's developer menu entries (keyed by the game's English text).</summary>
    internal static class MenuLabels
    {
        // Segoe Fluent Icons / Segoe MDL2 Assets code points.
        internal const string IconSettings = "";
        internal const string IconSearch = "";
        internal const string IconClose = "";
        internal const string IconChevronRight = "";
        internal const string IconChevronDown = "";
        internal const string IconCheck = "";
        internal const string Money = "$"; // drawn with the text font

        private static readonly Dictionary<string, string> Sections = new Dictionary<string, string>
        {
            { "Dev Menu Active", "Obecné" },
            { "Developer Spectation", "Divák" },
            { "Developer Call Takeover", "Převzetí hovoru" },
            { "Lobby Controls", "Lobby" },
            { "Computer Virus", "Viry v počítači" },
            { "Russian Speech", "Ruský hlas" },
            { "Daukey Menu", "Hráč a svět" },
            { "Game Time Warp", "Průběh hry" },
            { "Player Money", "Peníze" },
            { "Spawn Interactable Prefabs", "Spawn předmětů" },
            { "Cop Skins", "Skiny" },
            { "Item Skins", "Skiny předmětů" },
            { "Emotes", "Emoty" },
        };

        private static readonly Dictionary<string, string> SectionIcons = new Dictionary<string, string>
        {
            { "Dev Menu Active", IconSettings },
            { "Developer Spectation", "" },
            { "Developer Call Takeover", "" },
            { "Lobby Controls", "" },
            { "Computer Virus", "" },
            { "Russian Speech", "" },
            { "Daukey Menu", "" },
            { "Game Time Warp", "" },
            { "Player Money", Money },
            { "Spawn Interactable Prefabs", "" },
            { "Cop Skins", "" },
            { "Item Skins", "" },
            { "Emotes", "" },
        };

        private static readonly Dictionary<string, string> Texts = new Dictionary<string, string>
        {
            // Buttons
            { "Begin Boarding Now", "Zahájit nástup" },
            { "End Boarding Now", "Ukončit nástup" },
            { "End Game (Cops Win)", "Konec hry: vyhráli policajti" },
            { "End Game (Smugglers Win)", "Konec hry: vyhráli pašeráci" },
            { "End Game (Nobody Wins)", "Konec hry: nikdo nevyhrál" },
            { "Teleport Everyone To Me", "Teleportovat všechny ke mně" },
            { "Reset Vending Machine Timers", "Resetovat automaty" },
            { "Reset Popup Tutorials", "Resetovat nápovědy" },
            { "Reset Tutorial Video Progress", "Resetovat tutoriálová videa" },
            { "Reset Playtime Counter", "Vynulovat odehraný čas" },
            { "Give Me Winning Match XP", "Přidat XP za výhru" },
            { "Give Me +10,000 XP", "Přidat 10 000 XP" },
            { "Reset Levels", "Resetovat levely" },
            { "Pretend New Player", "Hrát jako nový hráč" },
            { "Credit All Players +$100", "Všem přidat $100" },
            { "Credit All Players +$100,000", "Všem přidat $100 000" },
            { "Reset My Money", "Vynulovat moje peníze" },
            { "Clear All Of My Spawned Items", "Smazat moje spawnuté předměty" },
            { "Close Menu", "Zavřít menu" },
            { "Unlock Random Skin", "Odemknout náhodný skin" },
            { "Unlock All Skins", "Odemknout všechny skiny" },
            { "Unlock Bag Skins Except 10 Each", "Odemknout skiny tašek (kromě 10)" },
            { "Equip Lobby Default Skins", "Výchozí skiny lobby" },
            { "Random Lobby Adjacent Skins", "Náhodné skiny lobby" },
            { "Clear All Skins", "Odebrat všechny skiny" },
            { "Unlock All Item Skins", "Odemknout všechny skiny předmětů" },
            { "Set All Item Skins Default", "Výchozí skiny předmětů" },
            { "Clear All Item Skins", "Odebrat skiny předmětů" },
            { "Unlock All Emotes", "Odemknout všechny emoty" },
            { "Clear All Emotes", "Odebrat všechny emoty" },
            { "Clear Equipped Emotes", "Sundat nasazené emoty" },
            { "Previous Day", "Předchozí den" },
            { "Next Day", "Další den" },
            { "Set Lobby Day", "Nastavit den" },
            { "Trigger Severe Virus", "Spustit silný virus" },
            { "Trigger Mild Virus", "Spustit slabý virus" },
            { "Clear Virus", "Odstranit virus" },
            { "Clear All Viruses", "Odstranit všechny viry" },
            { "Join Lobby as Spectator", "Připojit se jako divák" },
            { "Drop Call Takeover", "Pustit převzatý hovor" },
            { "Cinematic Fly Cam (Missing)", "Létající kamera (chybí)" },
            { "Clear", "Vymazat" },
            // Toggles
            { "Game UI", "Herní UI" },
            { "Is Agent", "Agent" },
            { "Player Speed", "Rychlost hráče 5×" },
            { "Cinematic Fly Cam", "Létající kamera" },
            { "Time Scale", "Zrychlený čas 5×" },
            { "Unlock All Apps", "Odemknout všechny appky" },
            { "Disable All Viruses", "Vypnout viry" },
            { "Take Next Answered Call", "Převzít další hovor" },
            { "Use Live Voice In Calls", "Živý hlas v hovorech" },
            // Labels
            { "Applies to everyone, including late joiners.", "Platí pro všechny, i pro později připojené." },
            { "No active workday", "Žádná směna neprobíhá" },
            { "Day (1-99)", "Den (1–99)" },
            { "Applies to your local Kolkata computer.", "Platí jen pro tvůj počítač." },
            { "Applies to the next Russian reply on this device.", "Platí pro další ruskou odpověď na tomhle PC." },
            { "Search", "Hledat" },
            { "Name", "Název" },
            { "Steam Lobby ID", "ID Steam lobby" },
            { "No Interactable spawn prefabs found.", "Žádné předměty ke spawnu." },
            { "No matching Interactable spawn prefabs found.", "Nic neodpovídá hledání." },
        };

        internal static string Section(string en) =>
            Lang.Czech && Sections.TryGetValue(en, out var cz) ? cz : en;

        internal static string Text(string en)
        {
            if (en == null || !Lang.Czech) return en;
            return Texts.TryGetValue(en, out var cz) ? cz : en;
        }

        internal static string SectionIcon(string en) =>
            SectionIcons.TryGetValue(en, out var icon) ? icon : IconChevronRight;

        internal static bool IsDanger(string en) =>
            en.StartsWith("Reset") || en.StartsWith("Clear") || en.StartsWith("End Game") || en.StartsWith("Drop ");

        internal static string Icon(string en, string section)
        {
            var s = en.ToLowerInvariant();
            if (s.StartsWith("selected: ")) return IconCheck;
            if (s.Contains("spectator")) return "";
            if (s.Contains("close menu")) return IconClose;
            if (s.Contains("previous day")) return "";
            if (s.Contains("next day")) return "";
            if (s.Contains("day")) return "";
            if (s.Contains("clear virus") || s.Contains("clear all viruses")) return "";
            if (s.Contains("virus")) return "";
            if (s.Contains("tts") || s.Contains("json default")) return "";
            if (s.Contains("boarding")) return "";
            if (s.Contains("end game")) return "";
            if (s.Contains("teleport")) return "";
            if (s.Contains("vending")) return "";
            if (s.Contains("tutorial")) return "";
            if (s.Contains("playtime")) return "";
            if (s.Contains("xp") || s.Contains("levels")) return "";
            if (s.Contains("new player")) return "";
            if (s.Contains("credit") || s.Contains("money")) return Money;
            if (s.StartsWith("clear")) return "";
            if (s.Contains("unlock")) return "";
            if (s.Contains("emote")) return "";
            if (s.Contains("skin")) return "";
            if (s.Contains("call") || s.StartsWith("join ")) return "";
            if (s.Contains("fly cam")) return "";
            if (s.Contains("spawn") || section == "Spawn Interactable Prefabs") return "";
            return IconChevronRight;
        }

        internal static string ToggleIcon(string en)
        {
            switch (en)
            {
                case "Game UI": return "";
                case "Is Agent": return "";
                case "Player Speed": return "";
                case "Cinematic Fly Cam": return "";
                case "Time Scale": return "";
                case "Unlock All Apps": return "";
                case "Disable All Viruses": return "";
                case "Take Next Answered Call": return "";
                case "Use Live Voice In Calls": return "";
                default: return IconSettings;
            }
        }

        /// <summary>Maps the game's toggle values to on/off; null when the value isn't a simple switch.</summary>
        internal static bool? ToggleState(string value)
        {
            switch (value)
            {
                case "ON": case "Enabled": case "5x": return true;
                case "OFF": case "Disabled": case "1x": return false;
                default: return null;
            }
        }
    }
}
