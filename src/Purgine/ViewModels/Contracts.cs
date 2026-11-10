using Purgine.Library;

namespace Purgine.ViewModels;

/// <summary>Acciones que una tarjeta de juego delega en la ventana principal.</summary>
public interface ICardHost
{
    bool CanLaunch { get; }
    void Launch(GameCardViewModel card);
    /// <summary>Forzar cierre: termina el juego en curso (árbol de procesos) y deja Purgine listo para jugar.</summary>
    void ForceClose(GameCardViewModel card);
    /// <summary>Diálogo de propiedades: DLC y mods de Workshop.</summary>
    void ShowProperties(GameCardViewModel card);
    /// <summary>Abre la ficha del juego (vista de detalle con héroe, logros y enlaces).</summary>
    void ShowDetails(GameCardViewModel card);
    /// <summary>Instala descargando de la CDN de Steam (SteamKit2, sin el cliente).</summary>
    void Install(GameCardViewModel card);
    /// <summary>Cancela la descarga en curso de este juego.</summary>
    void CancelInstall(GameCardViewModel card);
    /// <summary>Desinstala borrando sus archivos y su appmanifest (sin el cliente).</summary>
    void Uninstall(GameCardViewModel card);
    /// <summary>Actualiza descargando solo lo que cambió (sin el cliente).</summary>
    void Update(GameCardViewModel card);
    void CreateTagFor(GameCardViewModel card);
    void OpenInstallFolder(GameCardViewModel card);
    void ViewInStore(GameCardViewModel card);
    void EditLocal(GameCardViewModel card);
    void RemoveLocal(GameCardViewModel card);
}

/// <summary>Acciones que la tienda web (WebView2) delega en la ventana principal.</summary>
public interface IStoreActions
{
    /// <summary>
    /// Botón «Jugar»/«Instalar» de la web (enlace <c>steam://</c>): instalado → jugar; de la cuenta → descargar
    /// con el motor nativo; gratuito aún no añadido → pedir su licencia gratuita (SteamKit2) y descargar.
    /// </summary>
    void PlayOrInstallFromStore(string appId, string? name, bool installOnly);

    void ReportStatus(string text);
}

/// <summary>Diálogos modales; lo implementa la ventana principal (la VM no conoce WPF Window).</summary>
public interface IDialogService
{
    string? PromptText(string title, string message, string initial = "");
    /// <summary>Inicio de sesión en Steam (usuario/contraseña + Steam Guard). True si se completó.</summary>
    Task<bool> SteamLoginAsync();
    LocalGameDefinition? EditLocalGame(LocalGameDefinition? existing);
    /// <summary>Diálogo de propiedades de un juego (DLC y mods).</summary>
    void ShowGameProperties(GamePropertiesViewModel properties);
    bool Confirm(string title, string message);
}
