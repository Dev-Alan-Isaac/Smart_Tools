using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Smart_Tools.WPF.Models
{
    // Mock Data Generator - plain data only. The generating, writing and saving
    // live in Controllers/MockDataManager*.cs; the view only shows the result.

    /// <summary>What kind of value a column holds.</summary>
    public enum MockDataFieldType
    {
        RowNumber,
        Uuid,
        FirstName,
        LastName,
        FullName,
        Email,
        Username,
        Phone,
        Company,
        JobTitle,
        Street,
        City,
        Country,
        PostalCode,
        Date,
        DateTime,
        Integer,
        Decimal,
        Price,
        Boolean,
        Word,
        Sentence,
        Url,
        IpAddress,
        HexColor,
        CustomList,
        Iban,
        CreditCard,
        Isbn13,
    }

    /// <summary>The headings of the column-type drop-down, in the order they are listed.</summary>
    public enum MockDataTypeCategory { Basic, Person, Location, NumbersDates, Text, Web, Finance }

    public enum MockDataFormat { Csv, Json, Sql }

    /// <summary>Which language the names, cities and companies come from.</summary>
    public enum MockDataLocale { English, Spanish }

    /// <summary>How a value must be written: quoted text, a bare number, true/false, or null.</summary>
    public enum MockDataValueKind { Text, Number, Boolean, Null }

    /// <summary>Which extra inputs a column needs (drives what its row shows).</summary>
    public enum MockDataOptionKind { None, NumberRange, DateRange, List }

    /// <summary>One generated value.</summary>
    public readonly record struct MockDataValue(string? Text, MockDataValueKind Kind)
    {
        public static readonly MockDataValue Null = new(null, MockDataValueKind.Null);
        public static MockDataValue FromText(string s) => new(s, MockDataValueKind.Text);
        public static MockDataValue FromNumber(string s) => new(s, MockDataValueKind.Number);
        public static MockDataValue FromBoolean(bool b) => new(b ? "true" : "false", MockDataValueKind.Boolean);
    }

    /// <summary>A problem found in the setup. <see cref="FieldIndex"/> is -1 for problems that aren't about one column.</summary>
    public sealed record MockDataProblem(int FieldIndex, string Code);

    /// <summary>One column the user is building. Bound straight to the row in the view.</summary>
    public sealed class MockDataField : INotifyPropertyChanged
    {
        private string _name = "";
        private MockDataFieldType _type = MockDataFieldType.Word;
        private string _min = "";
        private string _max = "";
        private string _options = "";
        private int _blankPercent;
        private string? _blankText;
        private string _error = "";
        private string _minHint = "";
        private string _maxHint = "";
        private int _number;
        private string _listName = "";
        private string _savedListNote = "";

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get => _name; set => Set(ref _name, value ?? ""); }

        public MockDataFieldType Type
        {
            get => _type;
            set
            {
                if (_type == value) return;
                _type = value;
                if (value != MockDataFieldType.CustomList) SetListName("");
                OnPropertyChanged();
                OnPropertyChanged(nameof(TypeKey));
                OnPropertyChanged(nameof(OptionKind));
                OnPropertyChanged(nameof(UsesRange));
                OnPropertyChanged(nameof(IsNumberRange));
                OnPropertyChanged(nameof(IsDateRange));
                OnPropertyChanged(nameof(IsList));
            }
        }

        /// <summary>
        /// For a custom-list column filled from a list kept in Settings: that list's name ("" for a list typed
        /// in by hand). The values are copied into <see cref="Options"/> by the view before each run.
        /// </summary>
        public string ListName { get => _listName; set => SetListName(value ?? ""); }

        private void SetListName(string value)
        {
            if (_listName == value) return;
            _listName = value;
            OnPropertyChanged(nameof(ListName));
            OnPropertyChanged(nameof(TypeKey));
            OnPropertyChanged(nameof(IsSavedList));
            OnPropertyChanged(nameof(IsList));
        }

        /// <summary>
        /// What the type drop-down selects: "T:Iban" for a built-in type, "L:Departments" for one of the
        /// user's saved lists (the drop-down lists both). Unknown keys are ignored.
        /// </summary>
        public string TypeKey
        {
            get => IsSavedList ? "L:" + _listName : "T:" + _type;
            set
            {
                if (string.IsNullOrEmpty(value)) return;
                if (value.StartsWith("L:", StringComparison.Ordinal))
                {
                    SetListName(value[2..]);
                    Type = MockDataFieldType.CustomList;
                }
                else if (value.StartsWith("T:", StringComparison.Ordinal) && Enum.TryParse(value[2..], out MockDataFieldType t) && Enum.IsDefined(t))
                {
                    SetListName("");
                    Type = t;
                }
            }
        }

        public bool IsSavedList => _type == MockDataFieldType.CustomList && _listName.Length > 0;

        /// <summary>"From your list ... · 4 values", or why the list can't be used. Set by the view.</summary>
        public string SavedListNote { get => _savedListNote; set => Set(ref _savedListNote, value ?? ""); }

        /// <summary>Lower end (a number or a yyyy-MM-dd date). Empty = the type's default.</summary>
        public string Min { get => _min; set => Set(ref _min, value ?? ""); }

        /// <summary>Upper end. Empty = the type's default.</summary>
        public string Max { get => _max; set => Set(ref _max, value ?? ""); }

        /// <summary>For a custom list: the values, separated by commas or new lines.</summary>
        public string Options { get => _options; set => Set(ref _options, value ?? ""); }

        /// <summary>Share of rows (0-100) where this column is left empty.</summary>
        public int BlankPercent
        {
            get => _blankPercent;
            set
            {
                value = Math.Clamp(value, 0, 100);
                if (_blankPercent == value) return;
                _blankPercent = value;
                // A number set from code (a preset, saved settings) replaces what the box shows;
                // one that came from typing leaves the typed text alone until the box is left.
                if (_blankText != null && ParseBlank(_blankText) != value) _blankText = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BlankText));
            }
        }

        /// <summary>
        /// What the "blank %" box holds. Forgiving while typing: empty or non-digits mean 0, anything
        /// over 100 means 100. <see cref="NormalizeBlankText"/> tidies the text when the box is left.
        /// </summary>
        public string BlankText
        {
            get => _blankText ?? _blankPercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            set
            {
                _blankText = value ?? "";
                BlankPercent = ParseBlank(_blankText);
            }
        }

        public void NormalizeBlankText()
        {
            _blankText = null;
            OnPropertyChanged(nameof(BlankText));
        }

        private static int ParseBlank(string text)
        {
            string digits = new string(text.Where(char.IsAsciiDigit).Take(4).ToArray());
            return digits.Length == 0 ? 0 : Math.Clamp(int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture), 0, 100);
        }

        /// <summary>Plain-language reason this column can't be used yet (empty when fine). Set by the view.</summary>
        public string Error
        {
            get => _error;
            set
            {
                if (_error == value) return;
                _error = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => _error.Length > 0;

        /// <summary>Greyed text in the empty Min / Max boxes: the range used when left empty. Set by the view.</summary>
        public string MinHint { get => _minHint; set => Set(ref _minHint, value ?? ""); }

        public string MaxHint { get => _maxHint; set => Set(ref _maxHint, value ?? ""); }

        /// <summary>Position in the list (1-based) for the little number chip.</summary>
        public int Number { get => _number; set => Set(ref _number, value); }

        public MockDataOptionKind OptionKind => _type switch
        {
            MockDataFieldType.Integer or MockDataFieldType.Decimal or MockDataFieldType.Price => MockDataOptionKind.NumberRange,
            MockDataFieldType.Date or MockDataFieldType.DateTime => MockDataOptionKind.DateRange,
            MockDataFieldType.CustomList => MockDataOptionKind.List,
            _ => MockDataOptionKind.None,
        };

        /// <summary>Shows the Min / Max boxes (numbers or dates).</summary>
        public bool UsesRange => OptionKind is MockDataOptionKind.NumberRange or MockDataOptionKind.DateRange;

        public bool IsNumberRange => OptionKind == MockDataOptionKind.NumberRange;
        public bool IsDateRange => OptionKind == MockDataOptionKind.DateRange;
        /// <summary>Shows the box for typing the values (a column filled from a saved list shows a note instead).</summary>
        public bool IsList => OptionKind == MockDataOptionKind.List && !IsSavedList;

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A column as it is saved to disk (no UI state).</summary>
    public sealed class MockDataFieldSetting
    {
        public string? Name { get; set; }
        public MockDataFieldType? Type { get; set; }
        public string? Min { get; set; }
        public string? Max { get; set; }
        public string? Options { get; set; }
        public string? ListName { get; set; }
        public int? BlankPercent { get; set; }
    }

    /// <summary>
    /// Everything the generator needs besides the columns. Saved between sessions
    /// (the setup only - generated data is never stored). Missing keys keep defaults.
    /// </summary>
    public sealed class MockDataSettings
    {
        public MockDataFormat Format { get; set; } = MockDataFormat.Csv;
        public MockDataLocale Locale { get; set; } = MockDataLocale.English;
        public int RowCount { get; set; } = 100;
        public int Seed { get; set; } = 12345;
        public string TableName { get; set; } = "mock_data";
        public bool IncludeHeader { get; set; } = true;
        public bool OptionsCollapsed { get; set; }
        public List<MockDataFieldSetting>? Fields { get; set; }
    }

    /// <summary>A ready-made set of columns shown as a chip.</summary>
    public sealed record MockDataPreset(string Key, IReadOnlyList<MockDataFieldSetting> Fields);

    /// <summary>One entry of the "type" drop-down (labels come from the language files). <see cref="Group"/> is the heading it is listed under.</summary>
    public sealed record MockDataTypeChoice(MockDataFieldType Type, string Label, string Group, string ListName = "", bool IsHeader = false)
    {
        /// <summary>Matches <see cref="MockDataField.TypeKey"/>: a built-in type or one of the user's saved lists. Heading rows ("H:...") are never selected.</summary>
        public string Key => IsHeader ? "H:" + Group : ListName.Length > 0 ? "L:" + ListName : "T:" + Type;
    }
}
