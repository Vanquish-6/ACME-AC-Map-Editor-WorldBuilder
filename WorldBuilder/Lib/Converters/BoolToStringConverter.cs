using Avalonia.Data;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace WorldBuilder.Lib.Converters {
    public class BoolToStringConverter : IValueConverter {
        public static readonly BoolToStringConverter DocumentOrHistory = new("Original", "Edit");
        public static readonly BoolToStringConverter ShownOrHidden = new("On", "Off");
        public static readonly BoolToStringConverter ExportOrSkip = new("Export", "Skip");
        public static readonly BoolToStringConverter LockedOrUnlocked = new("Locked", "Lock");

        private readonly string _whenTrue;
        private readonly string _whenFalse;

        public BoolToStringConverter() : this("Yes", "No") { }

        public BoolToStringConverter(string whenTrue, string whenFalse) {
            _whenTrue = whenTrue;
            _whenFalse = whenFalse;
        }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
            if (value is bool boolValue && targetType == typeof(string)) {
                return boolValue ? _whenTrue : _whenFalse;
            }
            return BindingOperations.DoNothing;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }
}
