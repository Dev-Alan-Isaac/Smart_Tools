using System.ComponentModel;

namespace Smart_Tools.WPF.Models
{
    /// <summary>
    /// One unit. A value in this unit becomes the category's base unit with <c>value * ToBase + Offset</c>
    /// (the offset is only used by temperature).
    /// </summary>
    public sealed record ConvUnit(string Id, string Symbol, string EnglishName, double ToBase, double Offset = 0)
    {
        /// <summary>The name shown in lists. Currencies carry their own name, so they are not looked up in the language files.</summary>
        public string Name { get; init; } = EnglishName;
    }

    /// <summary>A group of units that convert into each other (length, mass, ...). Currency is the one that needs the network.</summary>
    public sealed record ConvCategory(string Id, string EnglishName, string Icon, IReadOnlyList<ConvUnit> Units, bool IsCurrency = false);

    /// <summary>One line of the "in every unit" table.</summary>
    public sealed class ConvRow : INotifyPropertyChanged
    {
        private string _value = "";

        public ConvRow(ConvUnit unit) => Unit = unit;

        public ConvUnit Unit { get; }
        public string Symbol => Unit.Symbol;
        public string Name => Unit.Name;

        public string Value { get => _value; set => Set(ref _value, value, nameof(Value)); }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T value, string name)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Exchange rates as last downloaded. Every rate is "units of this currency for 1 US dollar".</summary>
    public sealed class CurrencyRateSet
    {
        public string Base { get; set; } = "USD";

        /// <summary>The day the rates are for (the source publishes once per working day).</summary>
        public string Date { get; set; } = "";

        public DateTimeOffset FetchedAt { get; set; }

        public Dictionary<string, double> Rates { get; set; } = new();

        public Dictionary<string, string> Names { get; set; } = new();

        public bool HasRates => Rates.Count > 1;
    }

    /// <summary>A pair of units in one category, such as meter to foot. Never holds an amount.</summary>
    public sealed record ConvPair(string CategoryId, string FromId, string ToId);

    /// <summary>One chip in the Favorites or Recent list.</summary>
    public sealed record ConvPairItem(ConvPair Pair, string Label, string Tip);

    /// <summary>What the converter remembers between sessions: the setup and the pairs of units used, never any value typed.</summary>
    public sealed class UnitConverterSettings
    {
        public string CategoryId { get; set; } = "length";
        public Dictionary<string, string> From { get; set; } = new();
        public Dictionary<string, string> To { get; set; } = new();

        /// <summary>Pairs the person starred, newest first.</summary>
        public List<ConvPair> Favorites { get; set; } = new();

        /// <summary>Pairs used lately, newest first.</summary>
        public List<ConvPair> Recent { get; set; } = new();
    }
}
