using System.Globalization;
using System.Windows.Data;

namespace WhiteBehemoth.Yara.Converters;

public class ListToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is List<string> list)
            return string.Join(Environment.NewLine, list);
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
            return str.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries).ToList();
        return new List<string>();
    }
}
