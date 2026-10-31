using Smart_Tools.WPF.Models;
using System.Globalization;
using System.Text;

namespace Smart_Tools.WPF.Controllers
{
    // Mock Data Generator - column categories and the values that carry a check digit
    // (IBAN, card numbers, ISBN). Pure: no WPF.
    public static partial class MockDataManager
    {
        /// <summary>The heading a column type is listed under in the drop-down.</summary>
        public static MockDataTypeCategory CategoryOf(MockDataFieldType type) => type switch
        {
            MockDataFieldType.FirstName or MockDataFieldType.LastName or MockDataFieldType.FullName or MockDataFieldType.Email
                or MockDataFieldType.Username or MockDataFieldType.Phone or MockDataFieldType.Company or MockDataFieldType.JobTitle => MockDataTypeCategory.Person,
            MockDataFieldType.Street or MockDataFieldType.City or MockDataFieldType.Country or MockDataFieldType.PostalCode => MockDataTypeCategory.Location,
            MockDataFieldType.Date or MockDataFieldType.DateTime or MockDataFieldType.Integer or MockDataFieldType.Decimal => MockDataTypeCategory.NumbersDates,
            MockDataFieldType.Word or MockDataFieldType.Sentence => MockDataTypeCategory.Text,
            MockDataFieldType.Url or MockDataFieldType.IpAddress or MockDataFieldType.HexColor => MockDataTypeCategory.Web,
            MockDataFieldType.Price or MockDataFieldType.Iban or MockDataFieldType.CreditCard or MockDataFieldType.Isbn13 => MockDataTypeCategory.Finance,
            _ => MockDataTypeCategory.Basic,
        };

        private static string RandomDigits(MockRandom rng, int count)
        {
            var sb = new StringBuilder(count);
            for (int i = 0; i < count; i++) sb.Append((char)('0' + rng.Next(10)));
            return sb.ToString();
        }

        // ---- IBAN (ISO 13616: mod-97 check digits; Spanish ones also get their own account check digits) ----

        private static readonly string[] BritishBanks = { "NWBK", "BARC", "LOYD", "HBUK", "MIDL", "NAIA" };

        /// <summary>A valid IBAN: Spanish (24 characters) or British (22). Written without spaces.</summary>
        internal static string MakeIban(MockRandom rng, bool spanish)
        {
            string country, bban;
            if (spanish)
            {
                country = "ES";
                string bank = RandomDigits(rng, 4), branch = RandomDigits(rng, 4), account = RandomDigits(rng, 10);
                bban = bank + branch + SpanishCheckDigit("00" + bank + branch) + SpanishCheckDigit(account) + account;
            }
            else
            {
                country = "GB";
                bban = rng.Pick(BritishBanks) + RandomDigits(rng, 6) + RandomDigits(rng, 8);
            }
            return country + IbanCheckDigits(country, bban) + bban;
        }

        /// <summary>The two check digits that follow the country code ("98 - remainder of the number moved around, mod 97").</summary>
        internal static string IbanCheckDigits(string country, string bban)
        {
            var digits = new StringBuilder();
            foreach (char c in bban + country + "00")
            {
                if (char.IsAsciiDigit(c)) digits.Append(c);
                else digits.Append((char.ToUpperInvariant(c) - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            }
            return (98 - Mod97(digits.ToString())).ToString("00", CultureInfo.InvariantCulture);
        }

        internal static int Mod97(string digits)
        {
            int rest = 0;
            foreach (char c in digits) rest = (rest * 10 + (c - '0')) % 97;
            return rest;
        }

        /// <summary>The Spanish bank-account check digit of 10 digits (weights 1 2 4 8 5 10 9 7 3 6, 11 minus the sum mod 11).</summary>
        private static char SpanishCheckDigit(string tenDigits)
        {
            int[] weights = { 1, 2, 4, 8, 5, 10, 9, 7, 3, 6 };
            int sum = 0;
            for (int i = 0; i < 10; i++) sum += (tenDigits[i] - '0') * weights[i];
            int digit = 11 - sum % 11;
            return (char)('0' + (digit == 11 ? 0 : digit == 10 ? 1 : digit));
        }

        // ---- Card numbers (Luhn) ----

        /// <summary>A Visa, Mastercard or American Express shaped number whose last digit satisfies the Luhn check.</summary>
        internal static string MakeCardNumber(MockRandom rng)
        {
            string prefix;
            int length;
            switch (rng.Next(3))
            {
                case 0: prefix = "4"; length = 16; break;                                  // Visa
                case 1: prefix = (51 + rng.Next(5)).ToString(CultureInfo.InvariantCulture); length = 16; break;   // Mastercard 51-55
                default: prefix = rng.Next(2) == 0 ? "34" : "37"; length = 15; break;      // American Express
            }
            string body = prefix + RandomDigits(rng, length - prefix.Length - 1);
            return body + LuhnCheckDigit(body);
        }

        /// <summary>The digit that, appended to <paramref name="body"/>, makes the whole number pass the Luhn check.</summary>
        internal static char LuhnCheckDigit(string body)
        {
            int sum = 0;
            bool doubleIt = true;   // the digit next to the check digit is doubled
            for (int i = body.Length - 1; i >= 0; i--)
            {
                int d = body[i] - '0';
                if (doubleIt)
                {
                    d *= 2;
                    if (d > 9) d -= 9;
                }
                sum += d;
                doubleIt = !doubleIt;
            }
            return (char)('0' + (10 - sum % 10) % 10);
        }

        // ---- ISBN-13 (EAN-13 check digit) ----

        /// <summary>A valid ISBN-13 starting 978 (or 979), written as 13 digits.</summary>
        internal static string MakeIsbn13(MockRandom rng)
        {
            string body = (rng.Next(10) == 0 ? "979" : "978") + RandomDigits(rng, 9);
            int sum = 0;
            for (int i = 0; i < 12; i++) sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
            return body + (char)('0' + (10 - sum % 10) % 10);
        }
    }
}
