using Smart_Tools.WPF.Models;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Smart_Tools.WPF.Controllers
{
    /// <summary>
    /// Unit and currency converter - all the logic. Units are plain tables (value -> base unit -> target unit);
    /// currency rates come from the free Frankfurter service (European Central Bank data, no account), are cached
    /// on disk, and the cache is used when the network isn't there. Nothing the user types is ever sent.
    /// </summary>
    public static class UnitConverterManager
    {
        public const string CurrencyId = "currency";

        /// <summary>Where the rates come from. Only the base currency is sent.</summary>
        public const string RatesHost = "api.frankfurter.dev";

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        // =================================================================
        //  The unit tables
        // =================================================================

        private static ConvUnit U(string id, string symbol, string name, double toBase, double offset = 0) => new(id, symbol, name, toBase, offset);

        /// <summary>Every non-currency category. The base unit of each is the one with factor 1.</summary>
        public static IReadOnlyList<ConvCategory> Categories { get; } = new List<ConvCategory>
        {
            new("length", "Length", "RulerSquare", new[]
            {
                U("um", "µm", "Micrometer", 1e-6), U("mm", "mm", "Millimeter", 1e-3), U("cm", "cm", "Centimeter", 1e-2), U("m", "m", "Meter", 1),
                U("km", "km", "Kilometer", 1000), U("in", "in", "Inch", 0.0254), U("ft", "ft", "Foot", 0.3048), U("yd", "yd", "Yard", 0.9144),
                U("mi", "mi", "Mile", 1609.344), U("nmi", "nmi", "Nautical mile", 1852),
            }),
            new("mass", "Mass", "ScaleBalance", new[]
            {
                U("mg", "mg", "Milligram", 1e-6), U("g", "g", "Gram", 1e-3), U("kg", "kg", "Kilogram", 1), U("t", "t", "Metric ton", 1000),
                U("oz", "oz", "Ounce", 0.028349523125), U("lb", "lb", "Pound", 0.45359237), U("st", "st", "Stone", 6.35029318),
            }),
            new("volume", "Volume", "BeakerOutline", new[]
            {
                U("ml", "mL", "Milliliter", 1e-3), U("l", "L", "Liter", 1), U("m3", "m³", "Cubic meter", 1000),
                U("tsp", "tsp", "Teaspoon (US)", 0.00492892159375), U("tbsp", "tbsp", "Tablespoon (US)", 0.01478676478125),
                U("floz", "fl oz", "Fluid ounce (US)", 0.0295735295625), U("cup", "cup", "Cup (US)", 0.2365882365),
                U("pt", "pt", "Pint (US)", 0.473176473), U("qt", "qt", "Quart (US)", 0.946352946),
                U("gal", "gal", "Gallon (US)", 3.785411784), U("galuk", "gal UK", "Gallon (UK)", 4.54609),
            }),
            new("area", "Area", "VectorSquare", new[]
            {
                U("cm2", "cm²", "Square centimeter", 1e-4), U("m2", "m²", "Square meter", 1), U("ha", "ha", "Hectare", 10000),
                U("km2", "km²", "Square kilometer", 1e6), U("in2", "in²", "Square inch", 6.4516e-4), U("ft2", "ft²", "Square foot", 0.09290304),
                U("yd2", "yd²", "Square yard", 0.83612736), U("ac", "ac", "Acre", 4046.8564224), U("mi2", "mi²", "Square mile", 2589988.110336),
            }),
            new("temperature", "Temperature", "Thermometer", new[]
            {
                U("degC", "°C", "Celsius", 1), U("degF", "°F", "Fahrenheit", 5.0 / 9.0, -160.0 / 9.0), U("K", "K", "Kelvin", 1, -273.15),
            }),
            new("speed", "Speed", "Speedometer", new[]
            {
                U("ms", "m/s", "Meters per second", 1), U("kmh", "km/h", "Kilometers per hour", 1 / 3.6), U("mph", "mph", "Miles per hour", 0.44704),
                U("kn", "kn", "Knot", 1852.0 / 3600.0), U("fts", "ft/s", "Feet per second", 0.3048),
            }),
            new("time", "Time", "TimerOutline", new[]
            {
                U("ms", "ms", "Millisecond", 1e-3), U("s", "s", "Second", 1), U("min", "min", "Minute", 60), U("h", "h", "Hour", 3600),
                U("d", "d", "Day", 86400), U("wk", "wk", "Week", 604800), U("yr", "yr", "Year (365.25 days)", 31557600),
            }),
            new("data", "Digital storage", "Harddisk", new[]
            {
                U("bit", "bit", "Bit", 0.125), U("B", "B", "Byte", 1), U("KB", "KB", "Kilobyte (1,000 bytes)", 1e3), U("MB", "MB", "Megabyte", 1e6),
                U("GB", "GB", "Gigabyte", 1e9), U("TB", "TB", "Terabyte", 1e12), U("KiB", "KiB", "Kibibyte (1,024 bytes)", 1024),
                U("MiB", "MiB", "Mebibyte", 1048576), U("GiB", "GiB", "Gibibyte", 1073741824), U("TiB", "TiB", "Tebibyte", 1099511627776),
            }),
            new("energy", "Energy", "FlashOutline", new[]
            {
                U("J", "J", "Joule", 1), U("kJ", "kJ", "Kilojoule", 1000), U("cal", "cal", "Calorie", 4.184), U("kcal", "kcal", "Kilocalorie (food Calorie)", 4184),
                U("Wh", "Wh", "Watt-hour", 3600), U("kWh", "kWh", "Kilowatt-hour", 3.6e6), U("BTU", "BTU", "British thermal unit", 1055.05585262),
            }),
            new("power", "Power", "LightningBolt", new[]
            {
                U("W", "W", "Watt", 1), U("kW", "kW", "Kilowatt", 1000), U("MW", "MW", "Megawatt", 1e6),
                U("hp", "hp", "Horsepower (mechanical)", 745.69987158227), U("PS", "PS", "Metric horsepower", 735.49875),
            }),
            new("pressure", "Pressure", "GaugeFull", new[]
            {
                U("Pa", "Pa", "Pascal", 1), U("kPa", "kPa", "Kilopascal", 1000), U("bar", "bar", "Bar", 1e5), U("mbar", "mbar", "Millibar", 100),
                U("atm", "atm", "Atmosphere", 101325), U("psi", "psi", "Pound per square inch", 6894.757293168), U("mmHg", "mmHg", "Millimeter of mercury", 133.322387415),
            }),
        };

        public static ConvCategory? Find(string? id) => Categories.FirstOrDefault(c => c.Id == id);

        // =================================================================
        //  Maths
        // =================================================================

        public static double Convert(double value, ConvUnit from, ConvUnit to) =>
            ((value * from.ToBase + from.Offset) - to.Offset) / to.ToBase;

        /// <summary>
        /// Reads what the user typed: the current culture first, then plain "1,234.5" / "1234,5" styles, so a comma
        /// or a point both work as the decimal mark when it can only mean that.
        /// </summary>
        public static bool TryParse(string? text, out double value)
        {
            value = 0;
            string s = (text ?? "").Trim().Replace(" ", "").Replace(" ", "");
            if (s.Length == 0) return false;

            // Thousands separators are only accepted where the digits really are grouped by three ("1,234,567"),
            // so "1,5" is a decimal comma in any language, never fifteen.
            foreach (var culture in new[] { CultureInfo.CurrentCulture, CultureInfo.InvariantCulture })
            {
                var format = culture.NumberFormat;
                var styles = GroupingOk(s, format.NumberGroupSeparator, format.NumberDecimalSeparator)
                    ? NumberStyles.Float | NumberStyles.AllowThousands
                    : NumberStyles.Float;
                if (double.TryParse(s, styles, culture, out value) && IsFinite(value)) return true;
            }

            // "1234,5": a single comma with no point is a decimal comma.
            if (s.Count(c => c == ',') == 1 && !s.Contains('.') &&
                double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && IsFinite(value)) return true;

            value = 0;
            return false;

            static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        }

        /// <summary>True when the whole-number part either has no group separator or is grouped in threes after a first group of one to three digits.</summary>
        public static bool GroupingOk(string text, string groupSeparator, string decimalSeparator)
        {
            if (groupSeparator.Length == 0 || !text.Contains(groupSeparator, StringComparison.Ordinal)) return true;

            int decimalAt = decimalSeparator.Length > 0 ? text.IndexOf(decimalSeparator, StringComparison.Ordinal) : -1;
            string whole = (decimalAt >= 0 ? text[..decimalAt] : text).TrimStart('+', '-');
            string[] parts = whole.Split(groupSeparator);
            if (parts[0].Length is 0 or > 3) return false;
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].Length != 3) return false;
            return true;
        }

        /// <summary>
        /// A number for people: up to 10 significant digits without trailing zeros, grouped thousands, and
        /// scientific notation only for the very large and the very small.
        /// </summary>
        public static string Format(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "-";
            if (value == 0) return "0";

            // Rounding noise such as 0.30000000000000004 goes away at 10 significant digits.
            double abs = Math.Abs(value);
            if (abs >= 1e15 || abs < 1e-6) return value.ToString("0.#########E+0", CultureInfo.CurrentCulture);

            double rounded = double.Parse(value.ToString("G10", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return rounded.ToString("#,0.###############", CultureInfo.CurrentCulture);
        }

        /// <summary>"1 m = 3.28084 ft": the rule behind the answer, for the line under it.</summary>
        public static string Formula(ConvUnit from, ConvUnit to) => $"1 {from.Symbol} = {Format(Convert(1, from, to))} {to.Symbol}";

        // =================================================================
        //  Currency
        // =================================================================

        private static string CachePath => Path.Combine(
            AppPaths.DataRoot, "currency_rates.json");

        private static string SettingsPath => Path.Combine(
            AppPaths.DataRoot, "unit_converter.json");

        /// <summary>The currency category for a set of rates, or null when there aren't any.</summary>
        public static ConvCategory? BuildCurrency(CurrencyRateSet? rates)
        {
            if (rates is not { HasRates: true }) return null;
            var units = rates.Rates
                .OrderBy(r => r.Key, StringComparer.Ordinal)
                .Where(r => r.Value > 0)
                .Select(r => new ConvUnit(r.Key, r.Key, r.Key, 1.0 / r.Value)
                {
                    Name = rates.Names.TryGetValue(r.Key, out var n) && n.Length > 0 ? n : r.Key,
                })
                .ToList();
            return new ConvCategory(CurrencyId, "Currency", "CurrencyUsd", units, IsCurrency: true);
        }

        public static CurrencyRateSet? LoadCachedRates()
        {
            try
            {
                if (!File.Exists(CachePath)) return null;
                var set = JsonSerializer.Deserialize<CurrencyRateSet>(File.ReadAllText(CachePath), JsonOptions);
                return set is { HasRates: true } ? set : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Downloads today's rates and keeps them as the offline copy. Throws when the service can't be reached.</summary>
        public static async Task<CurrencyRateSet> FetchRatesAsync(CancellationToken token = default)
        {
            string latest = await Http.GetStringAsync($"https://{RatesHost}/v1/latest?base=USD", token).ConfigureAwait(false);
            var set = ParseRates(latest, DateTimeOffset.Now);

            // The names are a nice-to-have: codes still work without them.
            try
            {
                string names = await Http.GetStringAsync($"https://{RatesHost}/v1/currencies", token).ConfigureAwait(false);
                set.Names = ParseNames(names);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (token.IsCancellationRequested) throw;
                set.Names = LoadCachedRates()?.Names ?? new();
            }

            SaveRates(set);
            return set;
        }

        /// <summary>Reads the service's reply. The base currency is added with rate 1.</summary>
        public static CurrencyRateSet ParseRates(string json, DateTimeOffset fetchedAt)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var set = new CurrencyRateSet
            {
                Base = root.TryGetProperty("base", out var b) ? b.GetString() ?? "USD" : "USD",
                Date = root.TryGetProperty("date", out var d) ? d.GetString() ?? "" : "",
                FetchedAt = fetchedAt,
            };
            set.Rates[set.Base] = 1;
            if (root.TryGetProperty("rates", out var rates) && rates.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in rates.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double rate) && rate > 0)
                        set.Rates[p.Name] = rate;
            }
            return set;
        }

        public static Dictionary<string, string> ParseNames(string json)
        {
            var names = new Dictionary<string, string>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return names;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) names[p.Name] = p.Value.GetString() ?? "";
            return names;
        }

        private static void SaveRates(CurrencyRateSet set)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                string temp = CachePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(set, JsonOptions));
                File.Move(temp, CachePath, overwrite: true);
            }
            catch (Exception)
            {
                // The rates still work for this session; they just won't be there offline next time.
            }
        }

        // =================================================================
        //  Favorite and recent pairs
        // =================================================================

        public const int MaxFavorites = 20;
        public const int MaxRecent = 10;

        public static bool IsFavorite(IReadOnlyList<ConvPair> favorites, ConvPair pair) => favorites.Contains(pair);

        /// <summary>Stars the pair, or un-stars it when it is already there. Returns true when it is a favorite afterwards.</summary>
        public static bool ToggleFavorite(List<ConvPair> favorites, ConvPair pair)
        {
            if (favorites.Remove(pair)) return false;
            favorites.Insert(0, pair);
            if (favorites.Count > MaxFavorites) favorites.RemoveRange(MaxFavorites, favorites.Count - MaxFavorites);
            return true;
        }

        /// <summary>Puts the pair first in the recent list (once, whatever it was before) and keeps the list short. A pair from a unit to itself is not worth keeping.</summary>
        public static void Remember(List<ConvPair> recent, ConvPair pair)
        {
            if (pair.FromId == pair.ToId) return;
            recent.Remove(pair);
            recent.Insert(0, pair);
            if (recent.Count > MaxRecent) recent.RemoveRange(MaxRecent, recent.Count - MaxRecent);
        }

        /// <summary>
        /// Whether both units still exist. Currency pairs can only be checked once rates are known: without rates
        /// they are kept, so a stored USD to EUR pair does not vanish while offline.
        /// </summary>
        public static bool IsValid(ConvPair pair, CurrencyRateSet? rates)
        {
            if (pair.CategoryId == CurrencyId)
                return rates is not { HasRates: true } || (rates.Rates.ContainsKey(pair.FromId) && rates.Rates.ContainsKey(pair.ToId));

            var category = Find(pair.CategoryId);
            return category != null && category.Units.Any(u => u.Id == pair.FromId) && category.Units.Any(u => u.Id == pair.ToId);
        }

        /// <summary>Removes pairs whose units are gone and any repeats. Returns true when something was removed.</summary>
        public static bool Prune(List<ConvPair> pairs, CurrencyRateSet? rates)
        {
            int before = pairs.Count;
            var seen = new HashSet<ConvPair>();
            pairs.RemoveAll(p => !IsValid(p, rates) || !seen.Add(p));
            return pairs.Count != before;
        }
        /// <summary>"m → ft", from the unit symbols.</summary>
        public static string Label(ConvPair pair)
        {
            if (pair.CategoryId == CurrencyId) return $"{pair.FromId} → {pair.ToId}";
            var units = Find(pair.CategoryId)?.Units;
            string Symbol(string id) => units?.FirstOrDefault(u => u.Id == id)?.Symbol ?? id;
            return $"{Symbol(pair.FromId)} → {Symbol(pair.ToId)}";
        }

        // =================================================================
        //  Settings
        // =================================================================

        public static UnitConverterSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new UnitConverterSettings();
                return JsonSerializer.Deserialize<UnitConverterSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new UnitConverterSettings();
            }
            catch (Exception)
            {
                return new UnitConverterSettings();
            }
        }

        public static void SaveSettings(UnitConverterSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                string temp = SettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(temp, SettingsPath, overwrite: true);
            }
            catch (Exception)
            {
                // Remembering the setup is a convenience; losing it is not worth an error.
            }
        }
    }
}
