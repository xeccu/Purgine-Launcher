using System.Text.Json;
using System.Text.Json.Serialization;

namespace Purgine.Infrastructure;

/// <summary>
/// Persistencia en JSON plano con escritura atómica (archivo temporal + reemplazo),
/// para que un corte de luz o un cierre forzado nunca deje el archivo a medias.
/// </summary>
public static class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Carga el archivo o devuelve una instancia nueva si no existe o está corrupto.</summary>
    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(stream, Options) ?? new T();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo leer {path}; se usarán valores por defecto.", ex);
            TryBackupCorrupt(path);
        }
        return new T();
    }

    /// <summary>Carga el archivo y, si no existía, lo crea con los valores por defecto (útil para ajustes editables).</summary>
    public static T LoadOrCreate<T>(string path) where T : new()
    {
        var existed = File.Exists(path);
        var value = Load<T>(path);
        if (!existed) Save(path, value);
        return value;
    }

    public static void Save<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, value, Options);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo guardar {path}.", ex);
            try { File.Delete(tmp); } catch { /* ignorar */ }
        }
    }

    private static void TryBackupCorrupt(string path)
    {
        try { File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); }
        catch { /* ignorar */ }
    }
}
