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

/// <summary>Prioridad de CPU para los procesos de <c>steamwebhelper</c> mientras se juega.</summary>
public enum HelperPriority
{
    Normal,
    BelowNormal,
    Idle,
}

/// <summary>Presentación de la biblioteca.</summary>
public enum LibraryViewMode
{
    Grid,
    List,
}

/// <summary>
/// Ajustes del usuario. Se guardan en <c>settings.json</c> (carpeta de datos); se editan desde la
/// pestaña Ajustes o a mano.
/// </summary>
public sealed class LauncherSettings
{
    public const int MinCoverWidth = 110;
    public const int MaxCoverWidth = 240;

    /// <summary>Versión del esquema de ajustes (para migrar valores por defecto antiguos).</summary>
    public const int CurrentVersion = 2;

    public const string DefaultSteamArguments = "-silent -nofriendsui -nochatui -minimized -vgui -nolightbox";
    public const string LegacySteamArguments = "-silent -nofriendsui -nochatui";

    /// <summary>0 = archivo anterior a las migraciones (o recién creado).</summary>
    public int SettingsVersion { get; set; }

    // ===================== Steam =====================

    /// <summary>
    /// Argumentos con los que se arranca <c>steam.exe</c> cuando no está en ejecución (solo entonces:
    /// si Steam ya está abierto, no se pueden cambiar). <c>-no-browser</c> ya no funciona en el cliente
    /// moderno (steamwebhelper es obligatorio); <c>-vgui</c> y <c>-nolightbox</c> se ignoran en versiones
    /// recientes, pero no hacen daño.
    /// </summary>
    public string SteamClientArguments { get; set; } = DefaultSteamArguments;

    /// <summary>Prioridad de CPU de <c>steamwebhelper</c> mientras se juega.</summary>
    public HelperPriority SteamHelperPriority { get; set; } = HelperPriority.BelowNormal;

    /// <summary>Recortar periódicamente la RAM (working set) de Steam y steamwebhelper mientras se juega.</summary>
    public bool TrimSteamMemoryWhilePlaying { get; set; } = true;

    /// <summary>
    /// Experimental: cerrar steamwebhelper mientras se juega. Libera más RAM, pero desactiva el overlay,
    /// las notificaciones y la interfaz de Steam hasta que Steam lo relance.
    /// </summary>
    public bool KillSteamWebHelperWhilePlaying { get; set; }

    /// <summary>Segundos tras detectar el juego antes de limitar Steam (deja terminar la validación de licencia/DRM).</summary>
    public int SteamLimitDelaySeconds { get; set; } = 30;

    public CleanupPolicy SteamCleanupPolicy { get; set; } = CleanupPolicy.Always;

    /// <summary>Segundos de margen tras cerrar el juego para que Steam suba las partidas a la nube.</summary>
    public int CloudSyncGraceSeconds { get; set; } = 10;

    /// <summary>Tiempo máximo para el cierre ordenado (<c>steam.exe -shutdown</c>) antes de forzar.</summary>
    public int SteamShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>Tiempo máximo esperando a que el juego aparezca (actualizaciones, compilación de shaders...).</summary>
    public int GameStartTimeoutSeconds { get; set; } = 300;

    /// <summary>Margen tras desaparecer el último proceso del juego antes de dar la sesión por terminada.</summary>
    public int RelaunchGraceSeconds { get; set; } = 8;

    /// <summary>Procesos residuales que se fuerzan a cerrar si sobreviven al cierre ordenado.</summary>
    public string[] SteamProcessesToClean { get; set; } =
    [
        "steam", "steamwebhelper", "gameoverlayui", "gameoverlayui64",
        "steamerrorreporter", "steamerrorreporter64",
    ];

    // ===================== Ventana y bandeja =====================

    /// <summary>Al lanzar un juego, ocultar la ventana (a la bandeja o minimizada).</summary>
    public bool MinimizeOnLaunch { get; set; } = true;

    /// <summary>La X oculta la ventana en la bandeja en lugar de cerrar la aplicación.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Minimizar oculta la ventana en la bandeja (sin botón en la barra de tareas).</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Notificaciones de Windows al sincronizar con la nube y al cerrar Steam.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Render por software: evita cargar el pipeline D3D en el proceso. Requiere reiniciar.</summary>
    public bool UseSoftwareRendering { get; set; }

    // ===================== Biblioteca =====================

    public LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Grid;

    /// <summary>Filtro rápido de la biblioteca: solo juegos instalados (false = todos los de la cuenta).</summary>
    public bool LibraryInstalledOnly { get; set; }

    /// <summary>Ancho de las portadas en la cuadrícula (px). El alto mantiene la proporción 2:3.</summary>
    public int CoverWidth { get; set; } = 160;

    // ===================== Tienda =====================

    /// <summary>País para precios de la tienda (código ISO de 2 letras). Vacío = región de Windows.</summary>
    public string StoreCountryCode { get; set; } = string.Empty;

    /// <summary>
    /// Actualiza ajustes guardados por versiones anteriores. Solo cambia valores que el usuario no tocó
    /// (p. ej. los argumentos de Steam, si siguen siendo los antiguos por defecto). Devuelve true si cambió algo.
    /// </summary>
    public bool Migrate()
    {
        if (SettingsVersion >= CurrentVersion) return false;

        if (SettingsVersion < 2 &&
            string.Equals(SteamClientArguments?.Trim(), LegacySteamArguments, StringComparison.OrdinalIgnoreCase))
        {
            SteamClientArguments = DefaultSteamArguments;
        }

        SettingsVersion = CurrentVersion;
        return true;
    }
}
