namespace LightLauncher.Library;

/// <summary>Plataformas/tiendas soportadas. Solo Steam está implementado en esta versión.</summary>
public enum GamePlatform
{
    Steam,
}

/// <summary>Un juego instalado localmente, independiente de la tienda de origen.</summary>
public sealed record GameEntry
{
    /// <summary>Identificador único y estable entre tiendas, p. ej. <c>steam:570</c>.</summary>
    public required string Id { get; init; }
    public required GamePlatform Platform { get; init; }
    /// <summary>ID nativo de la tienda (AppID de Steam).</summary>
    public required string AppId { get; init; }
    public required string Name { get; init; }
    /// <summary>Carpeta de instalación; null si se desconoce (se detecta solo por Registro).</summary>
    public string? InstallDir { get; init; }
    public string? LibraryPath { get; init; }
    public long SizeOnDisk { get; init; }
    /// <summary>False si Steam lo marca como actualizándose / descarga incompleta.</summary>
    public bool IsFullyInstalled { get; init; } = true;
    /// <summary>Portada desde la caché local del cliente (sin red). Puede ser null.</summary>
    public string? CoverImagePath { get; init; }

    public static string MakeSteamId(string appId) => $"steam:{appId}";
}
