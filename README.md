# LightLauncher

Launcher de videojuegos ultraligero y Open Source (MIT) para Windows. Unifica tu biblioteca local y actúa
como **capa orquestadora fantasma**: abre el cliente oficial solo cuando hace falta, en modo mínimo, y lo
cierra al terminar de jugar para no dejar procesos en segundo plano comiendo RAM y CPU.

> **No es un emulador de DRM ni un crack.** Las licencias las sigue validando el cliente oficial (Steam).
> LightLauncher solo decide *cuándo* está abierto.

## Requisitos

- Windows 10/11 x64 (o x86)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) para compilar (`winget install Microsoft.DotNet.SDK.10`)
- Steam instalado

## Compilar y ejecutar

```bash
dotnet build
```

```bash
dotnet run --project src/LightLauncher
```

Ejecutable autocontenido de un solo archivo (no requiere .NET instalado en el equipo destino):

```bash
dotnet publish src/LightLauncher -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Cómo funciona

```
clic en un juego
   │
   ├─ ¿Steam abierto? ── no ──► steam.exe -silent -nofriendsui -nochatui  (espera a que esté listo)
   │
   ├─ steam.exe -- "steam://run/{appId}"        (protocolo oficial)
   │
   ├─ espera a que aparezca el juego:
   │     · procesos cuyo .exe vive en steamapps/common/<juego>   → evento Process.Exited
   │     · Registro HKCU\Software\Valve\Steam\RunningAppID       → fallback
   │
   ├─ cronómetro de sesión (Stopwatch + latido cada minuto en disco)
   │
   └─ al salir el juego (con margen para relanzamientos launcher→juego):
         · espera de sincronización en la nube
         · steam.exe -shutdown   (cierre ordenado: Steam termina de subir partidas)
         · kill de steam / steamwebhelper / gameoverlayui residuales
```

## Estructura

```
LightLauncher.sln
Directory.Build.props              Propiedades comunes (versión, licencia, nullable…)
src/LightLauncher/
├─ Core/
│  ├─ ProcessOrchestrator.cs       Ciclo de vida completo de la sesión (Fase 1)
│  ├─ GameProcessWatcher.cs        Detección de arranque/salida del juego
│  ├─ SteamLocator.cs              Ruta de Steam y estado vía Registro
│  └─ ProcessUtils.cs              Rutas de procesos (Win32), kill, recorte de memoria
├─ Library/
│  ├─ VdfParser.cs                 Parser KeyValues de Valve (.vdf / .acf)
│  ├─ SteamLibraryScanner.cs       libraryfolders.vdf + appmanifest_*.acf + portadas locales (Fase 2)
│  └─ GameEntry.cs                 Modelo de juego independiente de la tienda
├─ Stats/GameTimeTracker.cs        Tiempo jugado por sesión (Fase 3)
├─ Settings/LauncherSettings.cs    Ajustes editables (settings.json)
├─ Infrastructure/                 Rutas, log, JSON atómico, P/Invoke
├─ ViewModels/                     MVVM sin dependencias externas
└─ App.xaml / MainWindow.xaml      UI oscura minimalista
```

Sin paquetes NuGet: todo sale del runtime de .NET.

## Datos locales

Todo se guarda en `%LocalAppData%\LightLauncher\` (botón **Datos** en la app):

| Archivo | Contenido |
|---|---|
| `settings.json` | Ajustes (se crea con valores por defecto la primera vez) |
| `playtime.json` | Histórico de sesiones y tiempo total por juego |
| `lightlauncher.log` | Log de diagnóstico (rota a los 512 KB) |

**Modo portable:** crea un archivo vacío `portable.txt` junto a `LightLauncher.exe` y los datos irán a `data\`.

No se guardan contraseñas ni tokens de ningún tipo: la sesión de Steam la gestiona el propio cliente.

### Ajustes (`settings.json`)

| Clave | Por defecto | Qué hace |
|---|---|---|
| `SteamClientArguments` | `-silent -nofriendsui -nochatui` | Argumentos al arrancar Steam |
| `SteamCleanupPolicy` | `Always` | `Always` · `OnlyIfStartedByLauncher` · `Never` |
| `CloudSyncGraceSeconds` | `10` | Margen tras salir del juego antes de cerrar Steam |
| `SteamShutdownTimeoutSeconds` | `30` | Espera al cierre ordenado antes de forzar |
| `GameStartTimeoutSeconds` | `300` | Espera máxima a que arranque el juego |
| `RelaunchGraceSeconds` | `8` | Margen para juegos con launcher propio |
| `MinimizeOnLaunch` | `true` | Minimiza y recorta memoria al jugar |
| `UseSoftwareRendering` | `false` | UI por software (menos memoria de GPU en el proceso) |
| `SteamProcessesToClean` | `steam`, `steamwebhelper`, … | Procesos a forzar si sobreviven |

## Consumo de memoria

Medido en Windows 11 con 15 juegos y portadas:

| Estado | Working set |
|---|---|
| Ventana visible | ~35–145 MB (WPF + portadas decodificadas) |
| Minimizado / jugando | **~23 MB** |

El objetivo de < 30 MB se cumple mientras juegas (la ventana se minimiza y el proceso recorta su
memoria). Con la ventana abierta WPF no baja de ~35 MB; para ese caso, la vía sería una UI Win32/C++
nativa o cerrar la ventana a la bandeja durante la partida.

## Notas y limitaciones

- `-no-browser` ya no funciona en el cliente moderno de Steam (steamwebhelper es obligatorio),
  por eso no está en los argumentos por defecto.
- Si Steam ya estaba abierto con otro juego en marcha, no se cierra.
- «Dejar de vigilar» detiene el seguimiento **sin** cerrar Steam (el juego puede seguir abierto).
- Juegos con anti-cheat que corren elevados: no se puede enganchar `Process.Exited`; se usa sondeo
  y el Registro de Steam, con el mismo resultado.
- Por ahora solo Steam. `GameEntry`/`GamePlatform` están pensados para añadir Epic, EA, GOG…

## Licencia

[MIT](LICENSE)
