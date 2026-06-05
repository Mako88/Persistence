using System.ComponentModel;
using System.Reflection;

namespace Persistence.Extensions
{
    /// <summary>
    /// Extensions for enums
    /// </summary>
    public static class EnumExtensions
    {
        /// <summary>
        /// Get the value of the description attribute if it exists
        /// </summary>
        public static string? GetDescription(this Enum value)
        {
            var attribute = value.GetType().GetCustomAttribute<DescriptionAttribute>();

            if (attribute == null)
            {
                return null;
            }

            return attribute.Description;
        }

        extension(Enum value)
        {
            /// <summary>
            /// Get an enum of type T with the given description attribute value
            /// </summary>
            public static bool TryParseDescription<T>(string description, out T result, bool caseInsensitive = false) where T : struct, Enum
            {
                var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static);

                foreach (var field in fields)
                {
                    var attribute = field.GetCustomAttribute<DescriptionAttribute>();

                    if (attribute != null && attribute.Description.Equals(description, caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.CurrentCulture))
                    {
                        result = (T)field.GetValue(null)!;
                        return true;
                    }
                }

                result = default;
                return false;
            }
        }
    }
}
