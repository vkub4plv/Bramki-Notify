using System;
using System.Globalization;
using System.Windows.Data;

namespace Bramki_Notify
{
    public sealed class RelativeTimeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return "";
            if (values[0] is not DateTime dt) return "";
            if (values[1] is not DateTime now) now = DateTime.Now;

            var span = now - dt;
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;

            if (span.TotalSeconds < 1)
                return "przed chwilą";

            if (span.TotalDays >= 1)
            {
                var d = (int)Math.Floor(span.TotalDays);
                return $"{d} {DayWord(d)} temu";
            }

            if (span.TotalHours >= 1)
            {
                var h = (int)Math.Floor(span.TotalHours);
                return $"{h} {PolishForm(h, one: "godzinę", few: "godziny", many: "godzin")} temu";
            }

            if (span.TotalMinutes >= 1)
            {
                var m = (int)Math.Floor(span.TotalMinutes);
                return $"{m} {PolishForm(m, one: "minutę", few: "minuty", many: "minut")} temu";
            }

            var s = Math.Max(0, (int)Math.Floor(span.TotalSeconds));
            return $"{s} {PolishForm(s, one: "sekundę", few: "sekundy", many: "sekund")} temu";
        }

        private static string PolishForm(int n, string one, string few, string many)
        {
            n = Math.Abs(n);

            if (n == 1)
                return one;

            int last2 = n % 100;
            int last = n % 10;

            if (last >= 2 && last <= 4 && !(last2 >= 12 && last2 <= 14))
                return few;

            return many;
        }

        private static string DayWord(int n) => Math.Abs(n) == 1 ? "dzień" : "dni";

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}