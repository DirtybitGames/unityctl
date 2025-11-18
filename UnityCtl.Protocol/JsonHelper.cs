using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace UnityCtl.Protocol;

public static class JsonHelper
{
    public static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        Converters =
        {
            new StringEnumConverter(new CamelCaseNamingStrategy())
        }
    };

    public static string Serialize<T>(T value)
    {
        return JsonConvert.SerializeObject(value, Settings);
    }

    public static T? Deserialize<T>(string json)
    {
        return JsonConvert.DeserializeObject<T>(json, Settings);
    }

    public static object? Deserialize(string json, Type type)
    {
        return JsonConvert.DeserializeObject(json, type, Settings);
    }
}
