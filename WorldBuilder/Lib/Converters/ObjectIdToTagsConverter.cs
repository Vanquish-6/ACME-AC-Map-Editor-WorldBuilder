using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace WorldBuilder.Lib.Converters {
    /// <summary>
    /// Converts a uint object ID to its keyword tags string for tooltip display.
    /// Uses the static ObjectTagIndex instance.
    /// </summary>
    public class ObjectIdToTagsConverter : IValueConverter {
        /// <summary>
        /// The shared tag index instance. Set this before the converter is used.
        /// </summary>
        public static ObjectTagIndex? TagIndex { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) {
            if (value is uint objectId && TagIndex != null) {
                var tagString = TagIndex.GetTagString(objectId);
                if (tagString != null) return tagString;
            }
            return null; // No tooltip if no tags
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) {
            throw new NotSupportedException();
        }
    }
}
