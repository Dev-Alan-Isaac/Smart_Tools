namespace Smart_Tools.WPF.Models
{
    public enum CheckStatus { Unknown, Low, Close, Ok }

    /// <summary>One bar in the result: a part (CPU, GPU, RAM) against what the game asks.</summary>
    public sealed class CheckRow
    {
        public required string Part { get; init; }

        /// <summary>Your part and its figure ("Ryzen 5 5600X · 21,800").</summary>
        public required string Yours { get; init; }

        /// <summary>What the game asks for ("Intel Core i5-4460 · 4,900").</summary>
        public required string Needs { get; init; }

        /// <summary>"about 4.4 times what it asks".</summary>
        public required string Detail { get; init; }

        /// <summary>0 to 100 for the bar (100 = twice what the game asks).</summary>
        public double Percent { get; init; }

        public CheckStatus Status { get; init; }

        public string StatusName => Status.ToString();
    }
}
