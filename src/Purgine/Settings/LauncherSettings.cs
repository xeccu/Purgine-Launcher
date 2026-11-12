namespace Purgine.Settings;

/// <summary>Qué hacer con los procesos del cliente oficial cuando termina la sesión de juego.</summary>
public enum CleanupPolicy
{
    /// <summary>Cerrar siempre Steam al terminar (comportamiento "RAM fantasma = 0").</summary>
    Always,
    /// <summary>Cerrar Steam solo si fue Purgine quien lo arrancó.</summary>
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

/// <summary>Perfil gráfico de la interfaz (ver <c>Infrastructure/GraphicsTheme</c>).</summary>
/// <summary>Estilo visual (solo apariencia: la lógica es la misma en todos).</summary>
public enum UiSkin
{
    /// <summary>Inspirado en el cliente de Steam: cabecera con pestañas, azul marino y azul Steam.</summary>
    Steam,
    /// <summary>Inspirado en Epic Games Store: barra lateral, negro neutro, tarjetas redondeadas y cian.</summary>
    Epic,
}

public enum GraphicsPreset
{
    /// <summary>Rendimiento: plano, sin animaciones, sombras ni transparencias.</summary>
    Low,
    /// <summary>Solo para leer ajustes antiguos: se migra a <see cref="High"/> (ya no se ofrece).</summary>
    Medium,
    /// <summary>Ultra-moderno: paneles translúcidos (acrílico), sombras, brillos y animaciones.</summary>
    High,
}

/// <summary>Orden de la biblioteca (lista lateral y cuadrícula).</summary>
public enum LibrarySortMode
{
    Name,
    Playtime,
    /// <summary>Actividad reciente: última sesión, de más reciente a más antigua.</summary>
    Recent,
}

/// <summary>Tipo de contenido de la biblioteca (filtros «Juegos / Software / Herramientas»).</summary>
public enum LibraryContentType
{
    Game,
    Software,
    Tool,
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
    public const int CurrentVersion = 6;

    /// <summary>
    /// Solo los argumentos nativos: <c>-silent</c> deja la interfaz en la bandeja y <c>-nochatui</c> no abre el chat.
    /// Sin <c>-minimized</c>: con la ventana principal minimizada, Windows escondía los diálogos que dependen de
    /// ella (contrato de licencia, opciones de inicio) y no se podían aceptar.
    /// </summary>
    public const string DefaultSteamArguments = "-silent -nochatui";
    public const string LegacySteamArguments = "-silent -nofriendsui -nochatui";
    public const string V2SteamArguments = "-silent -nofriendsui -nochatui -minimized -vgui -nolightbox";
    public const string V3SteamArguments = "-silent -nofriendsui -nochatui -minimized -vgui -fulldesktopres 0";

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


    /// <summary>Notificaciones de Windows al sincronizar con la nube y al cerrar Steam.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Perfil gráfico (Bajo / Medio / Alto). Se aplica al instante.</summary>
    public GraphicsPreset GraphicsPreset { get; set; } = GraphicsPreset.High;

    /// <summary>Estilo visual: Steam o Epic Games (se combina con <see cref="GraphicsPreset"/>: Moderna / Optimizada).</summary>
    public UiSkin Skin { get; set; } = UiSkin.Steam;

    /// <summary>Última carpeta elegida para instalar juegos de itch.io (null = la de Purgine en AppData).</summary>
    public string? ItchLibraryPath { get; set; }

    /// <summary>Render por software: evita cargar el pipeline D3D en el proceso. Requiere reiniciar.</summary>
    public bool UseSoftwareRendering { get; set; }

    // ===================== Biblioteca =====================

    public LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Grid;

    /// <summary>Filtro rápido de la biblioteca: solo juegos instalados (false = todos los de la cuenta).</summary>
    public bool LibraryInstalledOnly { get; set; }

    /// <summary>Ancho de las portadas en la cuadrícula (px). El alto mantiene la proporción 2:3.</summary>
    public int CoverWidth { get; set; } = 160;

    /// <summary>Filtros por tipo: juegos (y demos), software y herramientas (servidores dedicados, SDK…).</summary>
    public bool LibraryShowGames { get; set; } = true;
    public bool LibraryShowSoftware { get; set; } = true;
    /// <summary>Desactivado por defecto, como en Steam: la mayoría son servidores dedicados y SDK gratuitos.</summary>
    public bool LibraryShowTools { get; set; }

    /// <summary>
    /// Launchers ocultos en la biblioteca (nombres de <see cref="Library.GamePlatform"/>). Se guarda lo oculto, no
    /// lo visible: un proveedor nuevo aparece activado sin tocar los ajustes.
    /// </summary>
    public List<string> LibraryHiddenPlatforms { get; set; } = [];

    /// <summary>Orden de la lista lateral y de la cuadrícula: actividad reciente, alfabético o tiempo jugado.</summary>
    public LibrarySortMode SortMode { get; set; } = LibrarySortMode.Name;

    /// <summary>
    /// Actualiza ajustes guardados por versiones anteriores. Solo cambia valores que el usuario no tocó
    /// (p. ej. los argumentos de Steam, si siguen siendo los antiguos por defecto). Devuelve true si cambió algo.
    /// </summary>
    public bool Migrate()
    {
        if (SettingsVersion >= CurrentVersion) return false;

        // Solo se sustituyen valores por defecto antiguos; los argumentos personalizados se respetan.
        var args = SteamClientArguments?.Trim();
        if (string.Equals(args, LegacySteamArguments, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args, V2SteamArguments, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(args, V3SteamArguments, StringComparison.OrdinalIgnoreCase))
        {
            SteamClientArguments = DefaultSteamArguments;
        }

        // v4: el perfil «Medio» desaparece; quien lo tenía pasa a Ultra-moderno (sustituye a Medio y Alto).
        if (GraphicsPreset == GraphicsPreset.Medium) GraphicsPreset = GraphicsPreset.High;

        // v5: comportamiento estilo Steam — la X oculta en la bandeja; minimizar va a la barra de tareas.
        if (SettingsVersion < 5) CloseToTray = true;

        SettingsVersion = CurrentVersion;
        return true;
    }
}
