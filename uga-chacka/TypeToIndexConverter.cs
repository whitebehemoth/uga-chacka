using System.Globalization;
using System.Windows.Data;

namespace uga_chacka;

public class TypeToIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string type)
        {
            return type == "FoundryLocal" ? 1 : 0;
        }
        return 0;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int index)
        {
            return index == 1 ? "FoundryLocal" : "OpenAI";
        }
        return "OpenAI";
    }
}
