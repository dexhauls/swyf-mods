using System.Text.RegularExpressions;

namespace CzechSpeech
{
    /// <summary>
    /// Removes text Whisper invents on silence or noise. Its Czech training data is full of subtitle credits,
    /// so quiet audio often comes out as "Titulky vytvořil JohnyX" and similar.
    /// </summary>
    internal static class TranscriptCleaner
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        /// <summary>Phrases removed wherever they appear.</summary>
        private static readonly Regex Junk = new Regex(
            @"titulky\s+(?:vytvo[řr]il[a]?|p[řr]ipravil[a]?|p[řr]elo[žz]il[a]?|z\b|pro\b|by\b)[^.!?\r\n]*[.!?]*" +
            @"|(?:(?:p[řr]eklad|korektura)(?:\s+a\s+titulky)?|titulky)\s*:\s*[^.!?\r\n]*[.!?]*" +
            @"|amara\.org[^.!?\r\n]*[.!?]*" +
            @"|d[ěe]kuj(?:i|eme)\s+za\s+(?:sledov[áa]n[íi]|zhl[ée]dnut[íi])(?:\s+a\b)?[.!]*" +
            @"|(?:nezapome[ňn]te\s+)?(?:odeb[íi]rejte|odeb[íi]rat\s+(?:kan[áa]l|n[áa]s))[^.!?\r\n]*[.!?]*" +
            @"|subtitles\s+by[^.!?\r\n]*[.!?]*",
            Options);

        /// <summary>Phrases removed only when they are the whole transcript (they could be said for real mid-sentence).</summary>
        private static readonly Regex JunkAlone = new Regex(
            @"^\s*(?:d[ěe]kuj(?:i|eme)\s+za\s+pozornost|d[ěe]kujeme|na\s+shledanou\s+u\s+dal[šs][íi]ho\s+d[íi]lu|you" +
            @"|(?:www\.)?[\w-]+(?:\.[\w-]+)*\.(?:cz|sk|org|com|net|eu))[\s.!…]*$",
            Options);

        private static readonly Regex NonLetters = new Regex(@"[^\p{L}\p{N}]+");

        /// <summary>Lowercase words only, for comparing text regardless of punctuation.</summary>
        private static string Words(string text) => NonLetters.Replace(text.ToLowerInvariant(), " ").Trim();

        /// <summary>A word or short phrase repeated five or more times in a row: Whisper's decoding loop.</summary>
        private static readonly Regex Loop = new Regex(@"\b(\w+(?:\s+\w+){0,3}?)(?:[\s,.…-]+\1\b){4,}", Options);

        private static readonly Regex Spaces = new Regex(@"\s{2,}");

        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            // On noise Whisper sometimes just repeats (part of) the prompt back.
            string prompt = Plugin.InitialPrompt.Value?.Trim();
            if (!string.IsNullOrEmpty(prompt))
            {
                text = text.Replace(prompt, " ");
                string words = Words(text);
                if (words.Length > 0 && (" " + Words(prompt) + " ").Contains(" " + words + " "))
                {
                    return string.Empty;
                }
            }

            string before = text;
            text = Junk.Replace(text, " ");
            bool removedJunk = text != before;
            text = Loop.Replace(text, "$1");
            text = Spaces.Replace(text, " ").Trim();
            if (JunkAlone.IsMatch(text))
            {
                return string.Empty;
            }

            // Whatever is left after cutting junk out must still look like speech, not a stray "a".
            int letters = 0;
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    letters++;
                }
            }
            return letters == 0 || (removedJunk && letters < 3) ? string.Empty : text;
        }
    }
}
