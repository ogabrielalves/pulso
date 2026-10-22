using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pulso.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex) { Paths.LogError("Json.Load " + path, ex); }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { Paths.LogError("Json.Save " + path, ex); }
    }
}
