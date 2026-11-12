using System.Text.Json;

namespace Purgine.Infrastructure;

/// <summary>Un perfil local de Purgine (cada persona con sus cuentas, ajustes y biblioteca personal).</summary>
public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    /// <summary>Foto del perfil (copiada dentro de la carpeta del perfil, para que no dependa del archivo original).</summary>
    public string? AvatarPath { get; set; }
    /// <summary>El primer perfil usa la carpeta de datos de siempre (no se mueve nada de lo que ya había).</summary>
    public bool UsesRootFolder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>Contenido de <c>profiles.json</c>.</summary>
public sealed class ProfileList
{
    public Guid ActiveId { get; set; }
    public List<UserProfile> Profiles { get; set; } = [];
}

/// <summary>
/// Perfiles locales (hasta <see cref="MaxProfiles"/>), en <c>profiles.json</c> de la carpeta de datos. Cada perfil
/// tiene su carpeta (<c>profiles\&lt;id&gt;</c>) con sus sesiones de Steam / Epic / itch.io, las cookies del
/// navegador, ajustes, etiquetas, tiempo jugado y notas: dos personas en el mismo equipo no se pisan. El primer
/// perfil (el de siempre) usa la carpeta raíz. Lo que es del equipo (juegos instalados, cachés) se comparte.
/// </summary>
public static class ProfileStore
{
    public const int MaxProfiles = 5;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath(string dataDir) => Path.Combine(dataDir, "profiles.json");

    public static ProfileList Load(string dataDir)
    {
        lock (Gate)
        {
            ProfileList? list = null;
            try
            {
                if (File.Exists(FilePath(dataDir)))
                    list = JsonSerializer.Deserialize<ProfileList>(File.ReadAllText(FilePath(dataDir)), Json);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { /* se regenera abajo */ }

            if (list is null || list.Profiles.Count == 0)
            {
                // Primera vez: el perfil de siempre, con el nombre del usuario de Windows y la carpeta actual.
                var first = new UserProfile { Name = Environment.UserName, UsesRootFolder = true };
                list = new ProfileList { ActiveId = first.Id, Profiles = [first] };
                Save(dataDir, list);
            }
            if (list.Profiles.All(p => p.Id != list.ActiveId)) list.ActiveId = list.Profiles[0].Id;
            return list;
        }
    }

    public static void Save(string dataDir, ProfileList list)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(dataDir);
            var tmp = FilePath(dataDir) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, FilePath(dataDir), overwrite: true);
        }
    }

    /// <summary>Carpeta de datos de un perfil.</summary>
    public static string DirFor(string dataDir, UserProfile profile) =>
        profile.UsesRootFolder ? dataDir : Path.Combine(dataDir, "profiles", profile.Id.ToString("N"));

    /// <summary>Carpeta del perfil activo (se resuelve una vez al arrancar).</summary>
    public static string ResolveActiveDir(string dataDir)
    {
        var list = Load(dataDir);
        var active = list.Profiles.First(p => p.Id == list.ActiveId);
        var dir = DirFor(dataDir, active);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Crea un perfil (máximo 5). Devuelve null o el motivo del fallo.</summary>
    public static string? Create(string dataDir, string name, string? avatarSource, out UserProfile? created)
    {
        created = null;
        name = name.Trim();
        if (name.Length == 0) return "Escribe un nombre para el perfil.";
        var list = Load(dataDir);
        if (list.Profiles.Count >= MaxProfiles) return $"Máximo {MaxProfiles} perfiles.";
        if (list.Profiles.Any(p => p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) return "Ya hay un perfil con ese nombre.";
        var profile = new UserProfile { Name = name };
        var dir = DirFor(dataDir, profile);
        Directory.CreateDirectory(dir);
        profile.AvatarPath = CopyAvatar(avatarSource, dir);
        list.Profiles.Add(profile);
        Save(dataDir, list);
        created = profile;
        return null;
    }

    /// <summary>Cambia el nombre y/o la foto. <paramref name="avatarSource"/> null = se deja la foto actual.</summary>
    public static string? Update(string dataDir, Guid id, string name, string? avatarSource)
    {
        name = name.Trim();
        if (name.Length == 0) return "Escribe un nombre para el perfil.";
        var list = Load(dataDir);
        var profile = list.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return "Ese perfil ya no existe.";
        if (list.Profiles.Any(p => p.Id != id && p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase))) return "Ya hay un perfil con ese nombre.";
        profile.Name = name;
        if (avatarSource is not null) profile.AvatarPath = CopyAvatar(avatarSource, DirFor(dataDir, profile)) ?? profile.AvatarPath;
        Save(dataDir, list);
        return null;
    }

    /// <summary>
    /// Borra un perfil que no es el activo, con su carpeta (sus sesiones, ajustes y biblioteca personal). Los juegos
    /// instalados no se tocan. El perfil de la carpeta raíz no se puede borrar.
    /// </summary>
    public static string? Delete(string dataDir, Guid id)
    {
        var list = Load(dataDir);
        var profile = list.Profiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return "Ese perfil ya no existe.";
        if (profile.Id == list.ActiveId) return "No se puede borrar el perfil que está en uso: cambia antes a otro.";
        if (profile.UsesRootFolder) return "El perfil principal no se puede borrar.";
        list.Profiles.Remove(profile);
        Save(dataDir, list);
        try
        {
            var dir = DirFor(dataDir, profile);
            if (Directory.Exists(dir) && dir.StartsWith(Path.Combine(dataDir, "profiles"), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Perfil quitado, pero su carpeta no se pudo borrar del todo: {ex.Message}";
        }
        return null;
    }

    public static void SetActive(string dataDir, Guid id)
    {
        var list = Load(dataDir);
        if (list.Profiles.All(p => p.Id != id)) return;
        list.ActiveId = id;
        Save(dataDir, list);
    }

    private static string? CopyAvatar(string? source, string profileDir)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;
        var ext = Path.GetExtension(source).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")) ext = ".png";
        // Nombre nuevo en cada cambio: la caché de imágenes de WPF no se queda con la foto anterior.
        foreach (var old in Directory.GetFiles(profileDir, "avatar-*.*")) { try { File.Delete(old); } catch (IOException) { } }
        var target = Path.Combine(profileDir, $"avatar-{DateTime.Now:yyyyMMddHHmmss}{ext}");
        File.Copy(source, target, overwrite: true);
        return target;
    }
}
