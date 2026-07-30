using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Safe reads from MCP / check_* JSON — avoids InvalidCastException on .Value&lt;T&gt;().
    /// </summary>
    public static class JTokenParsing
    {
        public static bool GetBool(JToken token, bool defaultValue = false)
        {
            if (token == null || token.Type == JTokenType.Null)
                return defaultValue;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return Math.Abs(token.Value<double>()) > double.Epsilon;
            if (bool.TryParse(token.ToString(), out var parsed))
                return parsed;
            return defaultValue;
        }

        public static long? GetLong(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                var value = token.Value<double>();
                if (value >= long.MinValue && value <= long.MaxValue)
                    return (long)Math.Round(value, MidpointRounding.AwayFromZero);
            }

            var text = token.ToString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        public static int? GetInt(JToken token)
        {
            var lng = GetLong(token);
            if (!lng.HasValue)
                return null;
            if (lng.Value < int.MinValue || lng.Value > int.MaxValue)
                return null;
            return (int)lng.Value;
        }

        public static double? GetDouble(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
                return token.Value<double>();

            var text = token.ToString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return null;
        }

        public static long? FirstLong(JToken parent, params string[] propertyNames)
        {
            if (parent == null || propertyNames == null)
                return null;

            foreach (var name in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var value = GetLong(parent[name]);
                if (value.HasValue)
                    return value;
            }

            return null;
        }
    }
}
