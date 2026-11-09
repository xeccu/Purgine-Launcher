# LightLauncher

Launcher de videojuegos ultraligero para Windows (Código visible, Todos los derechos reservados). Gestiona tu biblioteca de Steam
—licencias, descargas, actualizaciones y desinstalaciones— **hablando directamente con los servidores de
Steam**, sin abrir el cliente de escritorio. El cliente oficial solo se despierta para **jugar** y se cierra
al terminar.

> **No es un emulador de DRM ni un crack.** Los juegos se siguen ejecutando a través del cliente oficial
> de Steam, que valida la licencia. LightLauncher solo decide *cuándo* está abierto y se ocupa del resto.

## Requisitos

- Windows 10/11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) para compilar (`winget install Microsoft.DotNet.SDK.10`)
- Steam instalado (para jugar)
- Una cuenta de Steam (para ver la biblioteca completa y descargar)

## Compilar y ejecutar

```bash
dotnet build
```

```bash
dotnet run --project src/LightLauncher
```

El repositorio incluye un `nuget.config` con nuget.org, así que restaura los paquetes aunque el equipo no
tenga orígenes de NuGet configurados.

Ejecutable autocontenido de un solo archivo:

```bash
dotnet publish src/LightLauncher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Arquitectura

```
                    ┌──────────────────────────────── LightLauncher ───────────────────────────────┐
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
- `LoginID` propio: la sesión de LightLauncher convive con la del cliente de escritorio sin expulsarla.
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
   si está abierto, se espera a que se cierre (LightLauncher lo cierra al terminar cada partida).

La cola (una descarga a la vez) se guarda en `download-queue.json` y vuelve en pausa al reiniciar. El
progreso, la velocidad y el tiempo restante son los **bytes reales recibidos de la CDN**.

**Desinstalar** borra la carpeta del juego y su appmanifest (con Steam cerrado), sin pasar por el cliente.
**Juegos gratuitos** que aún no están en la cuenta: se pide su licencia gratuita por la red y se descargan.
**Comprar** sigue siendo en la web oficial de Steam (navegador).

### 4. Jugar (`Core/ProcessOrchestrator.cs`)

La **única** función de `steam.exe`:

1. Si Steam está cerrado, se abre en modo ligero (`-silent -nofriendsui -nochatui -minimized …`) y se oculta
   su ventana principal si aparece.
2. `steam://run/{appId}` (Steam valida la licencia y lanza el juego).
3. Vigilancia del proceso del juego (carpeta de instalación + registro de Steam), cronómetro de la sesión y,
   durante la partida, `steamwebhelper` en prioridad baja y recorte de la RAM de Steam.
4. Al salir: cuenta atrás para que Steam suba las partidas a la nube, `steam.exe -shutdown` y cierre de
   procesos residuales. Después se registran en Steam las descargas que esperaban.

### Otras funciones

- **Tiempo jugado**: histórico de Steam (`localconfig.vdf`) más las sesiones locales que Steam aún no ha
  volcado, sin doble conteo.
- **Tienda**: búsqueda, ofertas y fichas con los endpoints públicos de la tienda; compra y carrito en la web.
- **Organización**: etiquetas, filtros «Todos / Instalados», cuadrícula o lista, tamaño de portadas.
- **Ejecutables propios** (Godot, emuladores…): con portada y tiempo jugado.
- **Bandeja del sistema** con `Shell_NotifyIcon` (sin WinForms): en la bandeja ocupa ~5–10 MB de RAM.

## Estructura

```
LightLauncher.sln · nuget.config · Directory.Build.props
src/LightLauncher/
├─ SteamNet/        Red de Steam (SteamKit2): sesión, propiedad, motor de depots, cola, registro en Steam
├─ Core/            Orquestador de partidas, vigilancia de procesos, limitador de Steam, ventana de Steam
├─ Library/         Escáner de manifiestos locales, VDF/appinfo, tiempo de Steam, etiquetas, ejecutables locales
├─ Stats/           Tiempo jugado
├─ Store/           Cliente de la tienda (endpoints públicos)
├─ Settings/        Ajustes (settings.json)
├─ Infrastructure/  Rutas, log, JSON atómico, P/Invoke, bandeja, imágenes
├─ ViewModels/ · Views/   Interfaz WPF (MVVM, sin dependencias externas)
```

## Datos locales

Todo en `%LocalAppData%\LightLauncher\` (botón «Abrir carpeta de datos» en Ajustes):

| Archivo | Contenido |
|---|---|
| `steam-session.bin` | Refresh token de Steam cifrado con DPAPI (nunca la contraseña) |
| `owned-games.json` | Juegos de la cuenta según Steam (caché) |
| `download-queue.json` | Cola de descargas pendientes |
| `settings.json` · `library.json` · `playtime.json` | Ajustes, etiquetas/ejecutables locales, tiempo jugado |
| `lightlauncher.log` | Registro de diagnóstico |

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

## Licencia

Este proyecto es de código visible únicamente para fines de auditoría e inspección. Todos los derechos reservados © 2026 xeccu (XCC Studios). Ver el archivo [LICENSE](LICENSE) para más detalles.
