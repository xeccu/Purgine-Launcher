using LightLauncher.Library;

namespace LightLauncher.ViewModels;

/// <summary>Acciones que una tarjeta de juego delega en la ventana principal.</summary>
public interface ICardHost
{
    bool CanLaunch { get; }
    void Launch(GameCardViewModel card);
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

/// <summary>Acciones de la tienda que necesitan al orquestador o a la biblioteca.</summary>
public interface IStoreActions
{
    GameCardViewModel? FindInstalled(string appId);

    /// <summary>
    /// ¿Consta en este equipo que la cuenta tiene el juego? (instalado, o con tiempo/última partida en
    /// <c>localconfig.vdf</c>). Sin iniciar sesión no se puede saber con certeza: es una señal local.
    /// </summary>
    bool IsKnownOwned(string appId);

    /// <summary>
    /// Instala un juego descargándolo directamente: si es de la cuenta, a la cola; si es gratuito y aún no
    /// está en la cuenta, primero se pide su licencia gratuita (SteamKit2); si es de pago sin licencia,
    /// abre la compra en la web.
    /// </summary>
    Task InstallAsync(string appId, string name, bool isFree);

    /// <summary>Ficha web oficial en el navegador predeterminado (sin abrir el cliente de Steam).</summary>
    void OpenInBrowser(string appId);

    /// <summary>Carrito web de Steam en el navegador predeterminado.</summary>
    void OpenCart();

    void Play(GameCardViewModel card);
}

/// <summary>Diálogos modales; lo implementa la ventana principal (la VM no conoce WPF Window).</summary>
public interface IDialogService
{
    string? PromptText(string title, string message, string initial = "");
    /// <summary>Inicio de sesión en Steam (usuario/contraseña + Steam Guard). True si se completó.</summary>
    Task<bool> SteamLoginAsync();
    LocalGameDefinition? EditLocalGame(LocalGameDefinition? existing);
    bool Confirm(string title, string message);
}
