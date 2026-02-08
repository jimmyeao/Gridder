using System.Globalization;
using Gridder.Models;

namespace Gridder.Converters;

public class AnalysisStatusToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AnalysisStatus status)
        {
            return status switch
            {
                AnalysisStatus.NotAnalyzed => Colors.Gray,
                AnalysisStatus.Analyzing => Colors.Orange,
                AnalysisStatus.Analyzed => Colors.LimeGreen,
                AnalysisStatus.Error => Colors.Red,
                _ => Colors.Gray
            };
        }
        return Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class AnalysisStatusToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AnalysisStatus status)
        {
            return status switch
            {
                AnalysisStatus.NotAnalyzed => "---",
                AnalysisStatus.Analyzing => "...",
                AnalysisStatus.Analyzed => "OK",
                AnalysisStatus.Error => "ERR",
                _ => "---"
            };
        }
        return "---";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
            return b ? "Yes" : "No";
        return "No";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
