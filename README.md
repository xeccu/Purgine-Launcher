# Purgine

**Purgine Launcher** es un launcher de videojuegos ultraligero para Windows (código visible, todos los derechos reservados). Gestiona tu biblioteca de Steam
—licencias, descargas, actualizaciones y desinstalaciones— **hablando directamente con los servidores de
Steam**, sin abrir el cliente de escritorio. El cliente oficial solo se despierta para **jugar** y se cierra
al terminar.

> **No es un emulador de DRM ni un crack.** Los juegos se siguen ejecutando a través del cliente oficial
> de Steam, que valida la licencia. Purgine solo decide *cuándo* está abierto y se ocupa del resto.

## Requisitos

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) para compilar (`winget install Microsoft.DotNet.SDK.10`)
- Steam instalado (para jugar)
- Microsoft Edge WebView2 Runtime para la pestaña Tienda (viene con Windows 11; en Windows 10 se instala gratis)
- Una cuenta de Steam (para ver la biblioteca completa y descargar)

## Compilar y ejecutar

```bash
dotnet build
```

```bash
dotnet run --project src/Purgine
```

El repositorio incluye un `nuget.config` con nuget.org, así que restaura los paquetes aunque el equipo no
tenga orígenes de NuGet configurados.

Ejecutable autocontenido de un solo archivo:

```bash
dotnet publish src/Purgine -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Arquitectura

```
                    ┌──────────────────────────────── Purgine ───────────────────────────────┐
                    │                                                                              │
  Servidores CM ◄───┤ SteamSession ──► SteamOwnershipService   licencias → paquetes → apps (PICS)    │
  de Steam          │ (SteamKit2)      (owned-games.json)       nombre, carpeta, build pública      │
                    │      │                                                                       │
  CDN de Steam ◄────┤      └────────► DepotDownloadEngine      manifiestos + fragmentos (chunks)     │
                    │                 SteamDownloadManager     cola, pausa/reanudar, progreso real  │
                    │                        │                                                     │
                    │                        ▼                                                     │
  steamapps/ ◄──────┤                 AppManifestWriter        appmanifest_{id}.acf + depotcache    │
                    │                                                                              │
  steam.exe ◄───────┤ ProcessOrchestrator  SOLO para jugar:    steam://run → vigilar → nube →        │
                    │                                          cierre seguro (-shutdown)            │
                    └──────────────────────────────────────────────────────────────────────────────┘
```

### 1. Sesión de Steam (`SteamNet/SteamSession.cs`)

- Conexión directa a los servidores CM con [SteamKit2](https://github.com/SteamRE/SteamKit) 3.5.
- Inicio de sesión con usuario y contraseña + **Steam Guard** (aprobación en la app móvil, código de la app
  o código por correo), usando el flujo de autenticación moderno de Steam.
- **La contraseña nunca se guarda.** Solo se guarda el *refresh token* que emite Steam, cifrado con DPAPI
  (ámbito del usuario de Windows) en `steam-session.bin`. Se puede revocar desde la web de Steam →
  «Dispositivos autorizados», o con «Cerrar sesión» en Ajustes.
- `LoginID` propio: la sesión de Purgine convive con la del cliente de escritorio sin expulsarla.
- La conexión se abre bajo demanda y se cierra tras 3 minutos sin uso (y sin descargas en curso), para que
  el launcher siga ocupando el mínimo de memoria.

### 2. Biblioteca y propiedad (`SteamNet/SteamOwnershipService.cs`)

- Lista **exacta** de lo que posee la cuenta, consultada en Steam: licencias → paquetes (PICS) → apps (PICS),
  con nombre, tipo, carpeta de instalación y **build pública** de cada juego.
- Se guarda en `owned-games.json` para mostrar la biblioteca al instante y sin conexión.
- **Actualizaciones**: un juego instalado cuya build (`buildid` del appmanifest) difiere de la pública
  muestra «Actualizar».
- Sin sesión, la biblioteca usa una aproximación con las cachés locales del cliente (`librarycache`,
  `localconfig.vdf`, `appinfo.vdf`).

### 3. Descargas y actualizaciones (`SteamNet/DepotDownloadEngine.cs`, `SteamDownloadManager.cs`)

Patrón [DepotDownloader](https://github.com/SteamRE/DepotDownloader), 100 % en C#:

1. Info PICS de la app → depots para Windows (64 bits si hay), idioma de Steam (o inglés) y DLC propios.
2. Por depot: clave de descifrado, código de manifiesto y manifiesto desde la CDN.
3. **Plan incremental**: los archivos iguales al manifiesto instalado se saltan; del resto solo se descargan
   los fragmentos cuyo Adler32 no coincide con lo que ya hay en disco. Por eso **reanudar tras una pausa** y
   **actualizar** solo bajan lo que falta o ha cambiado.
4. Descarga en paralelo (8 fragmentos) con reintentos y rotación de servidores CDN; cada fragmento se
   escribe directamente en su posición del archivo.
5. Al terminar, `AppManifestWriter` registra el juego en Steam (`appmanifest_{id}.acf` con `StateFlags 4`,
   build e `InstalledDepots`, y los manifiestos en `Steam/depotcache`) para que el cliente oficial lo
   reconozca como instalado y al día. Se hace **con Steam cerrado** (abierto, reescribiría el manifiesto);
   si está abierto, se espera a que se cierre (Purgine lo cierra al terminar cada partida).

La cola (una descarga a la vez) se guarda en `download-queue.json` y vuelve en pausa al reiniciar. El
progreso, la velocidad y el tiempo restante son los **bytes reales recibidos de la CDN**.

**Desinstalar** borra la carpeta del juego y su appmanifest (con Steam cerrado), sin pasar por el cliente.
**Juegos gratuitos** que aún no están en la cuenta: se pide su licencia gratuita por la red y se descargan.
**Comprar** se hace en la tienda oficial de Steam, dentro de la pestaña Tienda.

### 4. Jugar (`Core/ProcessOrchestrator.cs`)

La **única** función de `steam.exe`:

0. **Contrato de licencia (EULA)**: antes de abrir nada, Purgine mira los contratos del juego (PICS,
   `common/eulas`: id, nombre, enlace y versión) y los que ya aceptaste (Steam los anota en
   `userdata\<cuenta>\config\localconfig.vdf` → `apps/<appid>` como `"<appid>_eula_<n>" "<versión>"`). Si falta
   alguno, lo enseña en su propio aviso con enlace para leerlo y **«Aceptar licencia y jugar»**. Con tu clic, y con
   Steam cerrado (si estaba abierto se cierra un momento), lo anota en `localconfig.vdf` igual que el cliente, con
   copia previa del archivo en `steam-config-backups`; Steam ya no muestra su ventana de contrato y el juego
   arranca. Nunca se acepta un contrato sin que lo pulses tú.
1. Si Steam está cerrado, se abre solo con sus argumentos nativos `-silent -nochatui`: la interfaz se queda en
   la bandeja de forma nativa. Purgine **no oculta ni modifica ninguna ventana de Steam** (sin `SW_HIDE`, sin
   cambiar estilos). Sin `-minimized`: con la ventana principal minimizada, Windows escondía los diálogos que
   dependen de ella y no se podían aceptar.
2. `steam://run/{appId}` (Steam valida la licencia y lanza el juego). Un monitor ligero detecta los **diálogos
   previos al juego** (contrato de licencia / EULA, acuerdos, términos, opciones de inicio DirectX / Vulkan /
   modo seguro, requisitos, conflicto de Steam Cloud…) y los trae al frente con el foco (`SW_SHOW` si Steam los
   tenía sin mostrar + `SetForegroundWindow`; si Windows no deja cambiar el foco, parpadean en la barra de
   tareas) para aceptar sin abrir el cliente. Mientras haya uno abierto, el plazo de arranque del juego se alarga.
3. Vigilancia del proceso del juego (carpeta de instalación + registro de Steam), cronómetro de la sesión y,
   durante la partida, `steamwebhelper` en prioridad baja y recorte de la RAM de Steam.
4. Al salir: cuenta atrás para que Steam suba las partidas a la nube, `steam.exe -shutdown` y cierre de
   procesos residuales. Después se registran en Steam las descargas que esperaban.

### 5. Biblioteca (`Views/LibraryView.xaml`, `Views/GameDetailView.xaml`)

Organizada como la biblioteca de un cliente de escritorio:

- **Lista lateral** virtualizada con todos los juegos (icono de 24 px + nombre), búsqueda rápida, filtros
  «Todos / Instalados» y orden alfabético o por tiempo de juego. Doble clic = botón principal.
- **Inicio**: cuadrícula o lista de portadas, con etiquetas y tamaño ajustable.
- **Ficha del juego** (clic en una portada o en la lista): imagen panorámica con degradado y logo, botón
  principal **JUGAR / INSTALAR / ACTUALIZAR**, **última sesión** y **tiempo de juego**, progreso de
  **logros** (servicio Player de Steam vía SteamKit2: desbloqueados/total y los más destacados) y enlaces a la
  página de la tienda, comunidad, discusiones, guías y soporte.
- **Secciones de la ficha** (`SteamNet/SteamCommunityService.cs`, solo lectura):
  - **Amigos que juegan**: quién lo juega ahora, quién jugó en las últimas 2 semanas y quién lo ha jugado,
    con avatar y nombre (servicio Player de Steam + perfiles). No cambia tu estado en línea.
  - **Cromos**: la colección del juego (Mercado de la Comunidad, público) marcando los que tienes en tu
    inventario, y tu nivel de insignia.
  - **Capturas y grabaciones**: miniaturas de tus capturas de Steam (F12) y acceso a sus carpetas.
  - **Comunidad**: guías más populares y últimas noticias y anuncios del juego.
  - **Notas** y **reseña** personales, guardadas solo en este equipo (`game-notes.json`). «Publicar en
    Steam…» copia el texto y abre la sección de reseñas de la página oficial: la publicas tú allí; Purgine
    nunca publica nada en tu nombre.
  - Los datos de Steam se guardan 10 minutos en memoria para no repetir consultas al volver a una ficha.
- **Forzar cierre**: si un juego se cuelga, el botón «FORZAR CIERRE» (junto a EN CURSO, en la barra de
  estado o en el menú del juego) termina el juego y todos sus procesos hijos, registra el tiempo jugado y deja
  Purgine «Listo para jugar» al momento; Steam se cierra después en segundo plano si los ajustes lo piden.
- **Propiedades…** (menú del juego o enlace de la ficha, juegos instalados):
  - **DLC**: los DLC que tienes; los que tienen archivos propios se pueden desactivar. Se guarda en el
    appmanifest (`UserConfig/DisabledDLC`, como el cliente de Steam, con copia `.purgine.bak`) y Steam y
    Purgine dejan de instalarlos y actualizarlos. Requiere Steam cerrado.
  - **Workshop**: los mods con nombre y tamaño; marcar o desmarcar se aplica **en disco al momento**, de la
    forma en que el juego los lee. Si el juego tiene su propia carpeta `mods` (como The Binding of Isaac, que
    copia ahí cada mod), se usa su archivo `disable.it`, el mismo que su menú de mods. Si no, la carpeta del mod
    sale de `steamapps\workshop\content\{appid}` a `steamapps\workshop\purgine-disabled\{appid}` (no se
    borra); Steam puede volver a descargarlo mientras sigas suscrito.
- Enlaces a **Steam Workshop** y a la **Tienda de puntos** del juego, siempre con su AppID. Solo aparecen si el
  juego tiene esa página según Steam (PICS: `workshop_visible` y cromos); sin ella Steam redirige a otra página.
  Los ejecutables locales no los muestran.
- **Espacio necesario**: los juegos no instalados muestran «Tamaño: X GB» en su portada y, en la ficha,
  «ESPACIO NECESARIO» junto a INSTALAR con el espacio libre del disco donde se instalaría (en rojo si no
  cabe). Es la suma de los depots que se descargarían en este equipo según PICS (Windows, 64 bits, idioma,
  DLC propios): la misma selección que usa el motor de descargas (`SteamNet/DepotSelector.cs`).
- **Arte** (`Library/SteamArtService.cs`): primero la caché que ya mantiene el cliente de Steam
  (`appcache\librarycache`), sin red; si falta, la CDN pública de Steam, guardando una copia en `art\`.
  Todo se decodifica al tamaño en que se muestra y la imagen panorámica se suelta al cambiar de juego o al
  ocultar la ventana.

### 6. Tienda (`Views/StoreView.xaml`)

- La tienda oficial (`store.steampowered.com`, en español) dentro de un **WebView2** (motor de Edge), con
  atrás/adelante/recargar/inicio y «abrir en el navegador». El inicio de sesión web y el carrito son los de
  Steam; el perfil del navegador se guarda en `webview\` (Purgine nunca ve ni guarda la contraseña, y el
  guardado de contraseñas del navegador está desactivado).
- Los botones de la web que abren el cliente (`steam://install`, `steam://run`…) los atiende Purgine: si el
  juego está instalado se juega; si es de la cuenta se descarga con el motor nativo; si es gratuito y aún no
  está en la cuenta se pide su licencia gratuita.
- **Consumo**: el navegador se crea al abrir la pestaña. Al salir de ella se **suspende** (sin CPU y con la
  memoria marcada como prescindible: de ~450 MB a ~35 MB en las pruebas) y, tras **60 s** fuera de la pestaña
  o al ocultar la ventana en la bandeja, se **cierra del todo** (sus procesos `msedgewebview2.exe`
  terminan). Al volver se reabre en la última página.

### 7. Calidad gráfica (`Infrastructure/GraphicsTheme.cs`, `Infrastructure/Fx.cs`)

Dos perfiles en Ajustes → «Calidad gráfica», aplicados al instante (recursos dinámicos, sin reiniciar):

| Perfil | Qué incluye |
|---|---|
| **Rendimiento** | Plano y minimalista: cero animaciones, cero sombras y cero transparencias. |
| **Ultra-moderno** (por defecto) | Material acrílico de Windows 11 detrás de la ventana con paneles flotantes translúcidos (`#141721`), bordes que se iluminan al pasar el cursor, esquinas redondeadas, sombras en portadas, barra de acción flotante con **brillo del color dominante del juego** seleccionado, botón JUGAR/INSTALAR con degradado (`#1A9FFF` → `#1072BA`) y brillo, portadas que se amplían con curva `CubicEase`, fundido cruzado de la imagen panorámica al cambiar de juego y transiciones de entrada entre vistas. En Windows 10 los paneles quedan sobre fondo opaco. |

Las animaciones solo se crean en Ultra-moderno y se retiran al terminar; con Purgine en la bandeja no se
dibuja nada en ningún perfil. Quien tuviera guardado el antiguo perfil «Medio» pasa a Ultra-moderno.

### 8. Diálogo de instalar

Al pulsar **INSTALAR** (biblioteca, ficha o botones de la tienda web) se abre un diálogo dentro de la propia
ventana, como el del cliente de Steam: mini-banner, nombre y tamaño del juego, casillas de **acceso directo
en el escritorio** y **en el menú Inicio** (marcadas por defecto) y la lista de unidades con su **espacio
libre**, con la de la biblioteca con más espacio preseleccionada (★). Las unidades sin espacio suficiente se
marcan y no se pueden elegir.

- Si la unidad elegida no tiene biblioteca de Steam, se crea `X:\SteamLibrary` y se registra en
  `steamapps\libraryfolders.vdf` (con copia de seguridad `libraryfolders.vdf.purgine.bak`) al registrar el
  juego, para que el cliente oficial lo vea.
- Los accesos directos apuntan a `Purgine.exe --run {appid}` (no a `steam://`), así la partida pasa por
  Purgine: Steam se abre solo para jugar y se cierra al terminar. Usan el icono del juego. Si Purgine ya está
  abierto, la petición se le pasa a esa instancia.

### 9. Epic Games

- **Biblioteca unificada**: los juegos instalados con Epic Games Launcher aparecen junto a los de Steam, leídos
  de sus manifiestos locales (`%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item`, sin red). Las
  portadas y la lista lateral llevan un distintivo **STEAM / EPIC / LOCAL**; filtros, orden, «Listos para
  jugar», notas, forzar cierre y tiempo jugado funcionan igual.
- **Cuenta de Epic** (Ajustes → «Cuenta de Epic Games»): inicias sesión en la página oficial de Epic dentro de
  una ventana de Purgine; Purgine no ve la contraseña, solo guarda el refresh token cifrado con DPAPI
  (`epic-session.bin`). Mismo enfoque que Legendary / Heroic.
- **Jugar sin Epic Games Launcher**: con la cuenta vinculada, Purgine ejecuta directamente el binario del
  juego con un código de intercambio de un solo uso (y el token de propiedad si el juego lo exige), así que el
  juego arranca con tu sesión de Epic y **sin ningún proceso del launcher**. Purgine nunca invoca
  `com.epicgames.launcher://`; si la cuenta no está vinculada, pide vincularla.
- **Tiempo jugado y arte**: el tiempo registrado en tu cuenta de Epic se suma al que mide Purgine (lanzando el
  juego directamente, Epic no lo registra), y el catálogo de la cuenta aporta portada, imagen panorámica y logo.
- **Tienda**: selector **Steam / Epic Games** en la pestaña Tienda (el mismo navegador para las dos). Los
  botones de la web de Epic que abrirían su launcher se bloquean (Purgine no lo abre).
- **Biblioteca completa**: con la cuenta vinculada aparecen también los juegos comprados o reclamados que no
  están instalados («No instalado», con portada del catálogo, tamaño e **INSTALAR**). Se omiten Unreal
  Engine/Fab, los contenidos descargables y lo que no es juego ni aplicación.
- **Instalar y actualizar sin Epic Games Launcher**: motor propio en C# (el mismo formato que interpreta
  Legendary): build pública y manifiesto binario de la cuenta, y descarga de los fragmentos desde la CDN de
  Epic (8 en paralelo, rotando entre CDN con reintentos), escritos directamente en cada archivo. Cola con
  pausar / reanudar / cancelar en la pestaña Descargas, diálogo de instalar con unidad (`X:\Epic Games`) y
  accesos directos. Reanudar y actualizar son incrementales: los archivos con el SHA-1 correcto no se vuelven
  a bajar. **ACTUALIZAR** aparece cuando la versión instalada no es la build pública. Lo instalado por
  Purgine se registra en `epic-installs.json` y se puede desinstalar desde Purgine; los juegos de Epic Games
  Launcher que Purgine actualiza siguen en su carpeta. Los pocos juegos con manifiesto antiguo (JSON) aún no
  se pueden instalar.
- **Logros**: la sección LOGROS de la ficha muestra tu progreso en Epic y los desbloqueados más raros, con la
  misma plantilla que Steam (definiciones y estado del jugador desde la cuenta vinculada).
- **Partidas en la nube sin su launcher**: los juegos que usan la nube de Epic Games Launcher (atributo
  `CloudSaveFolder` del catálogo) se sincronizan con esa misma nube, con la lógica de Steam Cloud. Tras cada
  sincronización se anota qué partida de la nube quedó y la huella (tamaño + SHA-1) de los archivos locales
  (`cloud-sync.json`). Al jugar y al cerrar: si solo cambió la nube, se descarga (con copia previa de lo local);
  si solo cambió lo local, se sube en segundo plano empaquetada en el formato de Epic (manifiesto y fragmentos;
  las partidas anteriores siguen en la nube); si **cambiaron las dos**, aparece el diálogo de conflicto. Aviso
  discreto al subir y una marca al terminar.
- **Conflicto de partidas** (como el de Steam): «Partida de este equipo» frente a «Partida en la nube», con día y
  hora de cada una, cuál es la más reciente y desde qué equipo se subió; eliges **Usar la de la nube** o **Usar la
  de este equipo** (o «no jugar ahora»). La que no eliges no se pierde: la local queda en las copias de Purgine y
  la de la nube en su historial. También salta al cerrar si, mientras jugabas, otro equipo subió una partida.
- **Steam Cloud**: Purgine lee el registro del cliente (`userdata\<cuenta>\<appid>\remotecache.vdf`: archivos,
  SHA-1 y número de cambio de la nube sincronizados) y consulta la nube con `Cloud.GetAppFileChangelist`, como
  hace Steam, para saber si cambió lo local, la nube o las dos. La ficha lo muestra («Steam Cloud al día»,
  «Cambios sin subir», «Nube más reciente», «Conflicto»). Antes de jugar, si va a bajar algo o hay conflicto, hace
  una copia de lo local; ante un conflicto enseña el mismo diálogo con los detalles y la elección se hace en la
  ventana de Steam, que es quien sincroniza esos juegos y mantiene su registro coherente (Purgine no escribe en
  la nube de Steam).
- **Carpeta de partidas**: se obtiene de la plantilla de Epic (`{AppData}`, `{UserDir}`, `{UserSavedGames}`,
  `{InstallDir}`, `{EpicID}`…), se elige a mano en la ficha o se deduce buscando el nombre del juego en las rutas
  habituales (Saved Games, Documentos, AppData, LocalLow). Cada descarga y cada cierre guardan una copia
  comprimida en `save-backups` (las 10 últimas por juego).
- **Indicador NUBE** en la ficha (Epic y Steam): «Nube sincronizada», «Sincronizando…», «Guardado local»,
  «Nube más reciente», «Conflicto», «Lo gestiona el juego» (los que guardan en sus propios servidores o con EOS)
  o «Sin conexión». Con un clic: elegir, abrir o restablecer la carpeta de partidas y abrir las copias.

### Otras funciones

- **Tiempo jugado**: histórico de Steam (`localconfig.vdf`) más las sesiones locales que Steam aún no ha
  volcado, sin doble conteo.
- **Organización**: etiquetas, filtros «Todos / Instalados», orden A-Z o por tiempo, cuadrícula o lista,
  tamaño de portadas.
- **Filtro por launcher**: desplegable «Launcher» con una casilla por plataforma (Steam, Epic Games, Local),
  generada a partir del registro de plataformas: un proveedor nuevo (GOG, Ubisoft, EA…) aparece solo. Se
  combina con los tipos, «Listos», etiquetas, búsqueda y orden, y se recuerda entre sesiones.
- **Recargar información de red** (icono ⟳ de la barra de la biblioteca y de la ficha): si tras una partida
  los logros, cromos o la conexión se quedan «cargando» o «sin conexión», cancela lo colgado, vacía las
  cachés de respuestas de Steam y Epic, reconecta las dos cuentas y vuelve a consultar propiedad,
  actualizaciones, tiempo jugado, logros, cromos, amigos y comunidad sin reiniciar Purgine.
- **Ejecutables propios** (Godot, emuladores…): con portada y tiempo jugado.
- **Reposo**: si Purgine se queda en segundo plano 45 s, compacta su memoria (GC + recorte del working set);
  igual al minimizar y al ocultarlo en la bandeja.
- **Bandeja del sistema** con `Shell_NotifyIcon` (sin WinForms), como Steam: la **X** oculta Purgine en la
  bandeja (menú «Abrir Purgine» / «Salir») y **minimizar** lo deja en la barra de tareas como cualquier
  ventana. En la bandeja ocupa ~10 MB de RAM.

## Estructura

```
Purgine.sln · nuget.config · Directory.Build.props
src/Purgine/
├─ SteamNet/        Red de Steam (SteamKit2): sesión, propiedad, motor de depots, cola, registro en Steam
├─ Core/            Orquestador de partidas, vigilancia de procesos, limitador de Steam, ventana de Steam
├─ Library/         Escáner de manifiestos locales, VDF/appinfo, tiempo de Steam, arte, etiquetas, ejecutables locales
├─ Stats/           Tiempo jugado
├─ Settings/        Ajustes (settings.json)
├─ Infrastructure/  Rutas, log, JSON atómico, P/Invoke, bandeja, imágenes
├─ ViewModels/ · Views/   Interfaz WPF (MVVM, sin dependencias externas)
```

## Datos locales

Todo en `%LocalAppData%\Purgine\` (botón «Abrir carpeta de datos» en Ajustes):

| Archivo | Contenido |
|---|---|
| `steam-session.bin` | Refresh token de Steam cifrado con DPAPI (nunca la contraseña) |
| `owned-games.json` | Juegos de la cuenta según Steam (caché) |
| `download-queue.json` | Cola de descargas pendientes (con la unidad y los accesos directos elegidos) |
| `settings.json` · `library.json` · `playtime.json` | Ajustes, etiquetas/ejecutables locales, tiempo jugado |
| `epic-session.bin` | Refresh token de Epic Games cifrado con DPAPI (nunca la contraseña) |
| `epic-installs.json` · `epic-download-queue.json` · `epic-builds.json` | Juegos de Epic instalados por Purgine, su cola de descargas y la caché de builds/tamaños |
| `save-paths.json` · `save-backups\` | Carpetas de partidas elegidas a mano y copias comprimidas de las partidas |
| `cloud-sync.json` | Última sincronización con la nube de Epic de cada juego (para detectar conflictos como Steam) |
| `steam-config-backups\` | Copias de `localconfig.vdf` de Steam hechas antes de anotar un contrato aceptado (las 10 últimas) |
| `game-notes.json` | Notas y reseñas personales por juego (solo locales) |
| `art\` | Iconos, imágenes panorámicas, logos e iconos de logros que no estaban en la caché de Steam |
| `webview\` | Perfil del navegador de la Tienda (cookies y sesión web de Steam, caché) |
| `purgine.log` | Registro de diagnóstico |

> Si vienes de una versión anterior (cuando la app se llamaba «LightLauncher»), al primer arranque la carpeta
> `%LocalAppData%\LightLauncher\` se mueve entera a `%LocalAppData%\Purgine\`: no pierdes ajustes, etiquetas,
> tiempo jugado ni la sesión de Steam.

## Limitaciones y avisos

- **Ni Valve ni Epic dan soporte oficial a clientes de terceros.** SteamKit2 y DepotDownloader se usan desde hace
  años con cuentas normales, pero el riesgo no es cero.
- Se descarga la **rama pública**. Las betas, los depots con manifiesto cifrado y el contenido del Workshop
  no se gestionan (Steam los resuelve al jugar).
- Los **redistribuibles compartidos** (DirectX, VC++…) que Steam instala aparte no se descargan; Steam los
  instala en el primer arranque si hacen falta.
- Si Steam detecta alguna diferencia con su propio registro, puede **verificar** el juego al lanzarlo
  (normalmente no descarga nada).
- La desinstalación borra los archivos **de forma permanente** (no van a la papelera).
- La interfaz se inspira en la organización de los clientes de escritorio (lista lateral, ficha con imagen
  panorámica), pero Purgine no usa logotipos ni marcas de Valve en su propia interfaz; el arte de cada juego
  es el que publica Steam para ese juego. Steam y el logotipo de Steam son marcas de Valve Corporation.

## Créditos

Desarrollado por **xeccu** · [XCC Studios](https://xccstudios.es)

## Licencia

Este proyecto es de código visible únicamente para fines de auditoría e inspección. Todos los derechos reservados © 2026 xeccu (XCC Studios). Ver el archivo [LICENSE](LICENSE) para más detalles.
