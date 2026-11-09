namespace LightLauncher.Settings;

/// <summary>Qué hacer con los procesos del cliente oficial cuando termina la sesión de juego.</summary>
public enum CleanupPolicy
{
    /// <summary>Cerrar siempre Steam al terminar (comportamiento "RAM fantasma = 0").</summary>
    Always,
    /// <summary>Cerrar Steam solo si fue LightLauncher quien lo arrancó.</summary>
    OnlyIfStartedByLauncher,
    /// <summary>No tocar nunca el cliente.</summary>
    Never,
}

/// <summary>
/// Ajustes del usuario. Se guardan en <c>settings.json</c> (carpeta de datos) y se pueden editar a mano.
/// </summary>
public sealed class LauncherSettings
{
    /// <summary>
    /// Argumentos con los que se arranca <c>steam.exe</c> cuando no está en ejecución.
    /// Nota: <c>-no-browser</c> ya no funciona en el cliente moderno de Steam (steamwebhelper es obligatorio),
    /// por eso no va por defecto.
    /// </summary>
    public string SteamClientArguments { get; set; } = "-silent -nofriendsui -nochatui";

    public CleanupPolicy SteamCleanupPolicy { get; set; } = CleanupPolicy.Always;

    /// <summary>Segundos de margen tras cerrar el juego para que Steam suba las partidas a la nube.</summary>
    public int CloudSyncGraceSeconds { get; set; } = 10;

    /// <summary>Tiempo máximo para el cierre ordenado (<c>steam.exe -shutdown</c>) antes de forzar.</summary>
    public int SteamShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>Tiempo máximo esperando a que el juego aparezca (actualizaciones, compilación de shaders...).</summary>
    public int GameStartTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Margen tras desaparecer el último proceso del juego antes de dar la sesión por terminada.
    /// Cubre el patrón "launcher del juego → ejecutable real".
    /// </summary>
    public int RelaunchGraceSeconds { get; set; } = 8;

    /// <summary>Minimiza la ventana (y recorta su memoria) al lanzar un juego.</summary>
    public bool MinimizeOnLaunch { get; set; } = true;

    /// <summary>Render por software: evita cargar el pipeline D3D en el proceso. Útil en equipos muy justos.</summary>
    public bool UseSoftwareRendering { get; set; }

    /// <summary>Procesos residuales que se fuerzan a cerrar si sobreviven al cierre ordenado.</summary>
    public string[] SteamProcessesToClean { get; set; } =
    [
        "steam", "steamwebhelper", "gameoverlayui", "gameoverlayui64",
        "steamerrorreporter", "steamerrorreporter64",
    ];
}
