namespace Smart_Tools.WPF.Models
{
    // =====================================================================
    //  Text to Speech data. Plain data - no System.Speech, no WPF.
    // =====================================================================

    public enum TextToSpeechState
    {
        Ready,
        Speaking,
        Paused,
    }

    /// <summary>An installed Windows voice.</summary>
    public sealed record TextToSpeechVoice(string Name, string Culture, string Gender, string Age)
    {
        /// <summary>"Zira · en-US · Female" - "Microsoft" and "Desktop" are noise in a short list.</summary>
        public string DisplayLabel
        {
            get
            {
                string shortName = Name.Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(" Desktop", "", StringComparison.OrdinalIgnoreCase).Trim();
                var parts = new List<string> { shortName.Length > 0 ? shortName : Name };
                if (Culture.Length > 0) parts.Add(Culture);
                if (Gender is "Male" or "Female") parts.Add(Gender);
                return string.Join(" · ", parts);
            }
        }

        public override string ToString() => DisplayLabel;
    }

    /// <summary>What to say and how - read from the sidebar when Speak or Save is pressed.</summary>
    public sealed record TextToSpeechRequest(string Text, TextToSpeechVoice? Voice, int Rate, int Volume, int Pitch);

    /// <summary>
    /// The SSML sent to the voice, plus where each character of the text
    /// sits inside it - the voice reports progress as positions in the SSML,
    /// and the view needs positions in the text box to highlight the word.
    /// </summary>
    public sealed class TextToSpeechPrompt
    {
        public required string Ssml { get; init; }

        /// <summary>For text index i: where that character starts in the SSML.</summary>
        public required int[] TextToSsml { get; init; }

        /// <summary>Where the spoken text starts in the text box (Speak selection starts mid-text).</summary>
        public int TextOffset { get; init; }
    }

    /// <summary>Remembered between sessions (text_to_speech.json in %AppData%\Smart Tools). No folders; no text.</summary>
    public sealed class TextToSpeechSettings
    {
        public string VoiceName { get; set; } = "";
        public int Rate { get; set; }
        public int Volume { get; set; } = 100;
        public int Pitch { get; set; }
        public bool HighlightWords { get; set; } = true;
        public bool SaveWithoutAsking { get; set; } = true;

        /// <summary>The name typed in "File name" (not a path). Empty = from the first words of the text.</summary>
        public string FileName { get; set; } = "";
    }
}
