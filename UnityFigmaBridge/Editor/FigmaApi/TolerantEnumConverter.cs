using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace UnityFigmaBridge.Editor.FigmaApi
{
    public class TolerantEnumConverter : StringEnumConverter
    {
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            try
            {
                return base.ReadJson(reader, objectType, existingValue, serializer);
            }
            catch (JsonSerializationException)
            {
                var enumType = Nullable.GetUnderlyingType(objectType) ?? objectType;
                if (enumType != objectType) return null;
                var values = Enum.GetValues(enumType);
                return values.Length > 0 ? values.GetValue(0) : Activator.CreateInstance(enumType);
            }
        }
    }
}
