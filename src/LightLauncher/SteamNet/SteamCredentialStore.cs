using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LightLauncher.Infrastructure;

namespace LightLauncher.SteamNet;

/// <summary>Sesión de Steam recordada. Nunca contiene la contraseña.</summary>
public sealed class SavedSteamSession
{
    public string AccountName { get; set; } = string.Empty;
    /// <summary>Refresh token emitido por Steam (renueva la sesión sin contraseña).</summary>
    public string RefreshToken { get; set; } = string.Empty;
    /// <summary>Datos de Steam Guard por equipo (evita pedir el código de correo cada vez).</summary>
    public string? GuardData { get; set; }
    public ulong SteamId64 { get; set; }
}

/// <summary>
/// Guarda la sesión de Steam cifrada con DPAPI (ámbito usuario actual de Windows): solo esta cuenta de
/// Windows en este equipo puede descifrarla. La contraseña no se guarda nunca: solo el refresh token
/// que Steam emite tras iniciar sesión (revocable desde la web de Steam → «Dispositivos autorizados»).
/// </summary>
public sealed class SteamCredentialStore
{
    /// <summary>Entropía adicional fija de la aplicación (no es un secreto; separa este blob de otros de DPAPI).</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LightLauncher.SteamSession.v1");

    private readonly string _path;

    public SteamCredentialStore(string path) => _path = path;

    public bool HasSession => File.Exists(_path);

    public SavedSteamSession? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return JsonSerializer.Deserialize<SavedSteamSession>(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Sesión de Steam ilegible (¿otro usuario de Windows?): {ex.Message}");
            return null;
        }
    }

    public void Save(SavedSteamSession session)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(session);
        try
        {
            var cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Clear()
    {
        try { File.Delete(_path); }
        catch (Exception ex) { Log.Warn($"No se pudo borrar la sesión guardada: {ex.Message}"); }
    }
}
