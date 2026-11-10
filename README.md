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

1. Si Steam está cerrado, se abre en modo ligero (`-silent -nofriendsui -nochatui -minimized …`) y se oculta
   su ventana principal si aparece.
2. `steam://run/{appId}` (Steam valida la licencia y lanza el juego).
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

### Otras funciones

- **Tiempo jugado**: histórico de Steam (`localconfig.vdf`) más las sesiones locales que Steam aún no ha
  volcado, sin doble conteo.
- **Organización**: etiquetas, filtros «Todos / Instalados», orden A-Z o por tiempo, cuadrícula o lista,
  tamaño de portadas.
- **Ejecutables propios** (Godot, emuladores…): con portada y tiempo jugado.
- **Bandeja del sistema** con `Shell_NotifyIcon` (sin WinForms): en la bandeja ocupa ~5–10 MB de RAM.

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
| `art\` | Iconos, imágenes panorámicas, logos e iconos de logros que no estaban en la caché de Steam |
| `webview\` | Perfil del navegador de la Tienda (cookies y sesión web de Steam, caché) |
| `purgine.log` | Registro de diagnóstico |

> Si vienes de una versión anterior (cuando la app se llamaba «LightLauncher»), al primer arranque la carpeta
> `%LocalAppData%\LightLauncher\` se mueve entera a `%LocalAppData%\Purgine\`: no pierdes ajustes, etiquetas,
> tiempo jugado ni la sesión de Steam.

## Limitaciones y avisos

- **Valve no da soporte oficial a clientes de terceros.** SteamKit2 y DepotDownloader se usan desde hace
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

## Licencia

Este proyecto es de código visible únicamente para fines de auditoría e inspección. Todos los derechos reservados © 2026 xeccu (XCC Studios). Ver el archivo [LICENSE](LICENSE) para más detalles.
