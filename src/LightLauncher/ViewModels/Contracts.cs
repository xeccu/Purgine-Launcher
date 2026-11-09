using LightLauncher.Library;

namespace LightLauncher.ViewModels;

/// <summary>Acciones que una tarjeta de juego delega en la ventana principal.</summary>
public interface ICardHost
{
    bool CanLaunch { get; }
    void Launch(GameCardViewModel card);
    /// <summary>Instalación en segundo plano (sin ventana de Steam).</summary>
    void Install(GameCardViewModel card);
    /// <summary>Alternativa: diálogo oficial de instalación de Steam.</summary>
    void InstallWithSteamDialog(GameCardViewModel card);
    /// <summary>Quita de la cola una instalación que aún no ha empezado a descargar.</summary>
    void CancelInstall(GameCardViewModel card);
    /// <summary>Desinstala con el comando oficial de Steam.</summary>
    void Uninstall(GameCardViewModel card);
    /// <summary>Aplica la actualización pendiente (despierta Steam, actualiza y lo cierra al terminar).</summary>
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
    /// Instala un juego: en segundo plano si es de la cuenta; con el diálogo de Steam si es gratuito y aún
    /// no está en la cuenta; y si es de pago sin licencia, abre la compra en la web.
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
    LocalGameDefinition? EditLocalGame(LocalGameDefinition? existing);
    bool Confirm(string title, string message);
}
