// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client.Host :: Program (host de consola del cliente)
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.IO;
using System.Text.Json;
using Fedo.StageLnk.Client;
using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Shared;
using Fedo.StageLnk.Visualizer;

// Banner de marca que se muestra al arrancar la consola.
const string Banner = """
    ╔══════════════════════════════════════════════╗
    ║         Fedo-StageLnk  CLIENTE  v1.0          ║
    ║   Consola local + sincronización de red       ║
    ╚══════════════════════════════════════════════╝
    """;

Console.OutputEncoding = System.Text.Encoding.UTF8; // Fuerza la consola a UTF-8 para símbolos y acentos.
Console.WriteLine(Banner); // Muestra el banner de presentación.

// Diagnóstico de herramientas externas
var (ffmpeg, ffprobe, ffplay) = ExternalTools.CheckAll();
Console.WriteLine($"[Diag] ffmpeg: {(ffmpeg ? "OK" : "NO ENCONTRADO")}  ffprobe: {(ffprobe ? "OK" : "NO ENCONTRADO")}  ffplay: {(ffplay ? "OK" : "NO ENCONTRADO")}");
if (!ffmpeg || !ffprobe || !ffplay)
    Console.WriteLine("Advertencia: faltan herramientas de FFmpeg. Instale FFmpeg y añádalo al PATH, o defina FEDO_FFMPEG/FEDO_FFPROBE/FEDO_FFPLAY.");
Console.WriteLine();

// Filtra los argumentos de línea de comandos que no empiezan por '-'.
var positional = args.Where(a => !a.StartsWith("-")).ToArray();
// El primer argumento posicional es el host del servidor; por defecto 127.0.0.1.
string host = positional.Length > 0 ? positional[0] : "127.0.0.1";
// El segundo argumento es el nombre del cliente; por defecto usa el nombre de la máquina.
string name = positional.Length > 1 ? positional[1] : $"Cliente-{Environment.MachineName}";
// El tercer argumento es la raíz de medios; sin él se usa la ruta por defecto de la biblioteca.
string mediaRoot = positional.Length > 2
    // Si se pasó, se usa la ruta indicada.
    ? positional[2]
    // Ruta por defecto dentro de la carpeta Library del cliente.
    : Path.Combine(Environment.CurrentDirectory, "Library", "Client", Environment.MachineName);
bool demo = args.Contains("--demo"); // Activa el modo demo si está presente el argumento.

// Configuración del render: %APPDATA%\Fedo.StageLnk.Client\client.json (monitor + fullscreen).
// La línea de comandos puede sobreescribirla: --monitor:<n>, --fullscreen, --no-fullscreen.
// Construye la ruta del archivo de configuración dentro de %APPDATA%.
string configPath = Path.Combine(
    // Carpeta de datos de aplicación del usuario actual.
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    // Subcarpeta del cliente y nombre del archivo JSON.
    "Fedo.StageLnk.Client", "client.json");
// Lee la configuración guardada (monitor y fullscreen) en forma de tupla.
var (configuredMonitor, configuredFullscreen) = LoadRenderConfig(configPath);

int monitorIndex = configuredMonitor; // Índice de monitor inicializado desde la configuración.
// Busca un argumento --monitor:<n> en la línea de comandos.
var monitorArg = args.FirstOrDefault(a => a.TrimStart('-').StartsWith("monitor:", StringComparison.OrdinalIgnoreCase));
// Comprueba si el usuario indicó un monitor concreto.
if (monitorArg is not null)
    // Parsea el número de monitor y actualiza monitorIndex.
    _ = int.TryParse(monitorArg.TrimStart('-')[8..], out monitorIndex);

bool fullscreen = configuredFullscreen; // Pantalla completa inicializada desde la configuración.
if (args.Contains("--fullscreen")) fullscreen = true; // Con --fullscreen fuerza pantalla completa.
if (args.Contains("--no-fullscreen")) fullscreen = false; // Con --no-fullscreen la desactiva.

var monitors = MonitorHelper.Enumerate(); // Obtiene la lista de monitores conectados.
Console.WriteLine("Monitores disponibles:"); // Cabecera de la lista de monitores.
// Recorre los monitores enumerados.
for (int i = 0; i < monitors.Count; i++)
    // Muestra índice (1-based), resolución y posición de cada monitor.
    Console.WriteLine($"  [{i + 1}] {monitors[i].Width}x{monitors[i].Height} en ({monitors[i].X},{monitors[i].Y})");

DisplayBounds? targetMonitor = null; // Monitor de destino del render; null = principal.
// Solo se puede elegir destino si existen monitores.
if (monitors.Count > 0)
{
    // Comprueba que el índice solicitado esté dentro del rango de monitores.
    if (monitorIndex >= 1 && monitorIndex <= monitors.Count)
        targetMonitor = monitors[monitorIndex - 1]; // Asigna el monitor solicitado (índice 1-based).
    // Si el monitor no existe pero no es el principal, se avisa.
    else if (monitorIndex != 0)
        // Avisa de que el monitor solicitado no existe.
        Console.WriteLine($"  (aviso) monitor {monitorIndex} no existe; el render irá al principal.");
}
// Nombre del monitor donde se hará el render.
string renderWhere = targetMonitor is not null ? $"[{monitorIndex}]" : "principal";
// Muestra el monitor, el modo de pantalla y la ruta de configuración utilizada.
Console.WriteLine($"Render en monitor {renderWhere}{ (fullscreen ? " (pantalla completa)" : "") } (config: {configPath})");

HybridEngine? hybrid = null; // Motor híbrido de reproducción (NAudio + render), aún sin crear.
IPlaybackEngine? engine = null; // Interfaz del motor de reproducción compartida.
ReactiveVisualizer? fx = null; // Visualizador audio-reactivo, nulo hasta que se abra.
// El motor real con audio solo está disponible en Windows.
if (OperatingSystem.IsWindows())
{
    hybrid = new HybridEngine(); // Crea el motor híbrido (NAudio + render).
    hybrid.TargetMonitor = targetMonitor; // Configura el monitor de destino.
    hybrid.Fullscreen = fullscreen; // Configura el modo de pantalla completa.
    // Habilita el streaming por HTTP: medios no locales se reproducen desde el servidor.
    hybrid.StreamBaseUrl = $"http://{host}:{Fedo.StageLnk.Protocol.ProtocolDefaults.HttpPort}";
    // Suscribe el evento que detecta los beats de graves.
    hybrid.BeatDetected += beat =>
    {
        // Muestra la fuerza del beat y la energía de graves.
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FX BEAT fuerza={beat.Strength:F2} graves={beat.BassEnergy:F1}");
        fx?.Feed(beat); // Envía el beat al visualizador audio-reactivo.
    };
    // Suscribe el evento que entrega el espectro de frecuencias.
    hybrid.SpectrumAvailable += spectrum =>
    {
        // Muestra el pico del espectro y la barra de niveles.
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FX espectro pico={spectrum.PeakFrequencyHz:F0}Hz mag={spectrum.PeakMagnitude:F3}  {SpectrumBar(spectrum.Bins)}");
        fx?.Feed(spectrum); // Envía el espectro al visualizador audio-reactivo.
    };
    engine = hybrid; // Usa el motor híbrido como motor de reproducción.
}
else
{
    // En sistemas no Windows se avisa de que se usará el stub de consola.
    Console.WriteLine("Motor NAudio solo disponible en Windows; se usa el stub de consola.");
}

if (demo) // Si está activo el modo demo...
    DemoLocal.Ensure(mediaRoot); // Genera los medios de demostración en la carpeta raíz.

// Crea el cliente StageLnk con nombre, raíz de medios, host y motor opcional.
await using var client = new StageLnkClient(name, mediaRoot, host, engine: engine);
// Usa el canal HTTP (puerto 9003) para la sincronización de medios: bytes
// crudos con Range y descarga en paralelo (reanudable) en lugar del TCP base64.
client.HttpBaseUrl = $"http://{host}:{Fedo.StageLnk.Protocol.ProtocolDefaults.HttpPort}";
// Suscribe el evento de log para imprimir cada mensaje con marca de tiempo.
client.Log += m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
// Avisa por consola cuando el cliente está sincronizado y listo.
client.SyncedAndReady += () => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] >>> LISTO. Esperando comandos de show...");

// En Windows se puede abrir y cerrar el visualizador al recibir cues.
if (OperatingSystem.IsWindows())
{
    // Suscribe el evento de recepción de cues.
    client.CueReceived += cue =>
    {
        // Muestra el número y el nombre de la cue recibida.
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] >>> Ejecutando cue {cue.Number:000} '{cue.Name}'");
        // Si la cue es de efectos visuales...
        if (cue.Kind == CueKind.VisualFx)
        {
            // Solo se abre el visualizador si aún no existe.
            if (fx is null)
            {
                // Crea el visualizador audio-reactivo con el nombre de la cue.
                fx = ReactiveVisualizer.Create($"FX {cue.Number:000} · {cue.Name}", targetMonitor, fullscreen);
                // Confirma la apertura del visualizador 3D de partículas OpenGL.
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FX audio-reactivo abierto (3D OpenGL).");
            }
        }
        // Si la cue es de audio, el FX FFT permanece abierto para reaccionar a él.
        else if (cue.Kind == CueKind.Audio)
        {
            // Informa de que el FX seguirá reaccionando a este audio.
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] FX audio-reactivo reaccionando al audio '{cue.Name}'.");
        }
        else
        {
            fx?.Close(); // Cierra el visualizador si existe.
            fx = null; // Anula la referencia al visualizador.
        }
    };
    // Suscribe el evento de cambio de estado del cliente.
    client.StatusChanged += s =>
    {
        // Muestra el nuevo estado.
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Estado: {s}");
        // Si el cliente se detiene...
        if (s == ClientStatus.Stopped)
        {
            fx?.Close(); // Cierra el visualizador.
            fx = null; // Anula la referencia al visualizador.
        }
    };
}
else
{
    // En plataformas sin Windows solo se muestran mensajes de texto.
    client.CueReceived += cue => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] >>> Ejecutando cue {cue.Number:000} '{cue.Name}'");
    // Muestra por consola los cambios de estado.
    client.StatusChanged += s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Estado: {s}");
}

using var cts = new CancellationTokenSource(); // Fuente de cancelación para detener el cliente.
// Al pulsar Ctrl+C cancela la ejecución en lugar de cerrar de golpe.
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Inicia el cliente de forma asíncrona con la señal de cancelación.
await client.StartAsync(cts.Token);

Console.WriteLine(); // Línea en blanco para separar la ayuda de los comandos.
// Muestra la lista de comandos disponibles (primera línea).
Console.WriteLine("Comandos: play <n> | playfile <ruta|nombre> | text <texto> | loadfile <ruta|nombre> | media | cues | status");
// Muestra la lista de comandos de control (segunda línea).
Console.WriteLine("          stop | pause | resume | next | prev | freeze | black | fade <s> | wait <s> | help | exit");
Console.WriteLine(); // Línea en blanco de separación.

// Reúne los comandos pasados como argumentos --cmd:<comando>.
var commands = args
    // Filtra los argumentos que empiezan por cmd:.
    .Where(a => a.TrimStart('-').StartsWith("cmd:", StringComparison.OrdinalIgnoreCase))
    // Extrae el texto del comando quitando el prefijo cmd:.
    .Select(a => a.TrimStart('-')[4..])
    .ToList(); // Convierte el resultado en una lista de comandos.
// Busca un argumento --cmds-file:<ruta> con un archivo de comandos.
var cmdsFile = args.FirstOrDefault(a => a.TrimStart('-').StartsWith("cmds-file:", StringComparison.OrdinalIgnoreCase))?.TrimStart('-')[10..];
// Si se indicó un archivo de comandos y existe...
if (cmdsFile is not null && File.Exists(cmdsFile))
    // Añade las líneas no vacías del archivo a la lista de comandos.
    commands.AddRange(File.ReadAllLines(cmdsFile).Where(l => !string.IsNullOrWhiteSpace(l)));

int cmdDelayMs = 0; // Retardo inicial entre comandos en milisegundos.
// Busca un argumento --cmd-delay:<ms> en la línea de comandos.
var delayArg = args.FirstOrDefault(a => a.TrimStart('-').StartsWith("cmd-delay:", StringComparison.OrdinalIgnoreCase));
// Si se indicó un retardo...
if (delayArg is not null)
    // Parsea el retardo y actualiza cmdDelayMs.
    _ = int.TryParse(delayArg.TrimStart('-')[10..], out cmdDelayMs);

// Si hay comandos cargados y retardo configurado...
if (commands.Count > 0 && cmdDelayMs > 0)
    await Task.Delay(cmdDelayMs); // Espera antes de ejecutar el primer comando.

// Ejecuta cada comando de la lista.
foreach (string cmd in commands)
{
    // Comprueba si el usuario pidió cancelar la ejecución.
    if (cts.IsCancellationRequested)
        break; // Sale del bucle si hay cancelación.
    // Hace eco del comando que se va a ejecutar.
    Console.WriteLine($"> {cmd}");
    // Ejecuta el comando de forma asíncrona.
    await ExecuteCommandAsync(client, cmd);
}

// Bucle principal de lectura de comandos desde la consola.
while (!cts.IsCancellationRequested)
{
    string? line = Console.ReadLine(); // Lee una línea escrita por el usuario.
    // Si no hay entrada (por ejemplo, consola redirigida)...
    if (line is null)
    {
        await Task.Delay(200); // Espera un poco para no consumir CPU.
        continue; // Repite el bucle.
    }
    // Ejecuta la línea leída como comando.
    await ExecuteCommandAsync(client, line);
}

Console.WriteLine("Cliente finalizado."); // Mensaje de cierre al terminar el bucle.

// Ejecuta un comando de texto contra el cliente StageLnk.
static async Task ExecuteCommandAsync(StageLnkClient client, string line)
{
    // Separa la línea en palabras ignorando espacios en blanco extra.
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) return; // Sin palabras no hay comando que ejecutar.

    // Despacha según la primera palabra (el comando), en minúsculas.
    switch (parts[0].ToLowerInvariant())
    {
        // Comando play <n>: reproduce la cue n de la lista local.
        case "play" when parts.Length > 1 && int.TryParse(parts[1], out int n):
            await client.PlayCueAsync(n); // Reproduce la cue n de forma asíncrona.
            break; // Termina este caso.
        // Comando playfile <ruta|nombre>: reproduce un archivo local.
        case "playfile" when parts.Length > 1:
            client.PlayLocalFile(string.Join(' ', parts[1..])); // Reproduce el archivo indicado.
            break; // Termina este caso.
        // Comando text <texto>: muestra un cartel de texto 3D en pantalla.
        case "text" when parts.Length > 1:
            await client.ShowTextAsync(string.Join(' ', parts[1..])); // Envía el texto al motor de render.
            break; // Termina este caso.
        // Comando loadfile <ruta|nombre>: verifica un archivo local sin reproducirlo.
        case "loadfile" when parts.Length > 1:
            ResolveLocalFile(client, string.Join(' ', parts[1..]), loadOnly: true); // Valida el archivo en modo carga.
            break; // Termina este caso.
        // Comando media: lista los medios locales (manifiesto + disco).
        case "media":
            // Recorre los medios locales del cliente.
            foreach (var m in client.ListLocalMedia())
            {
                // Dimensiones del medio, o '-' si no tiene resolución.
                string dim = m.Width > 0 ? $"{m.Width}x{m.Height}" : "-";
                // Imprime tipo, tamaño, duración, dimensiones y ruta de cada medio.
                Console.WriteLine($"  {m.Kind,-8} {m.Size,12}  dur={m.DurationSeconds,4}s  {dim,-10} {m.RelativePath}");
            }
            // Muestra el número total de medios locales.
            Console.WriteLine($"  Total: {client.ListLocalMedia().Count} medios locales");
            break; // Termina este caso.
        // Comando cues: lista la cue list recibida del servidor.
        case "cues":
            // Recorre todas las cues de la lista.
            foreach (var cue in client.CueList)
            {
                // Construye la descripción de las capas de cada cue.
                string layers = string.Join(", ", cue.Layers.Select(l => $"z{l.ZOrder}:{l.MediaId?[..Math.Min(8, l.MediaId.Length)]}"));
                // Imprime número, nombre, tipo y capas de la cue.
                Console.WriteLine($"  {cue.Number:000} {cue.Name,-20} [{cue.Kind}]{(layers.Length > 0 ? "  " + layers : "")}");
            }
            // Muestra el número total de cues.
            Console.WriteLine($"  Total: {client.CueList.Count} cues");
            break; // Termina este caso.
        // Comando status: muestra el estado del cliente.
        case "status":
            // Imprime el estado actual y la última cue ejecutada.
            Console.WriteLine($"  Estado: {client.Status}  Última cue: {client.LastCueNumber:000}");
            // Imprime el nombre del motor y sus FPS.
            Console.WriteLine($"  Motor: {client.EngineName}  FPS: {client.EngineFps:F1}");
            // Imprime la carpeta raíz de medios.
            Console.WriteLine($"  MediaRoot: {client.MediaRoot}");
            // Imprime el espacio libre en disco.
            Console.WriteLine($"  Disco libre: {FreeDiskMb(client.MediaRoot)} MB");
            break; // Termina este caso.
        // Comando stop: detiene la reproducción actual.
        case "stop": client.Stop(); break;
        // Comando pause: pausa la reproducción actual.
        case "pause": client.Pause(); break;
        // Comando resume: reanuda la reproducción actual.
        case "resume": client.Resume(); break;
        // Comando freeze: congela la imagen actual.
        case "freeze": client.Freeze(); break;
        // Comando black: apaga la salida a negro.
        case "black": client.BlackOut(); break;
        // Comando fade <s>: funde a negro en <s> segundos.
        case "fade" when parts.Length > 1 && double.TryParse(parts[1], out double fadeS):
            client.Fade(fadeS, toBlack: true); // Aplica el fade a negro con la duración indicada.
            break; // Termina este caso.
        // Comando next: salta a la siguiente cue.
        case "next": await client.NextAsync(); break;
        // Comando prev: vuelve a la cue anterior.
        case "prev": await client.PreviousAsync(); break;
        // Comando wait <s>: espera <s> segundos.
        case "wait" when parts.Length > 1 && double.TryParse(parts[1], out double waitS):
            await Task.Delay((int)(waitS * 1000)); // Convierte los segundos a milisegundos y espera.
            break; // Termina este caso.
        // Comando help: muestra la ayuda completa de comandos.
        case "help":
            // Ayuda: play <n> reproduce la cue n de la cue list local.
            Console.WriteLine("  play <n>             reproduce la cue n de la cue list local");
            // Ayuda: playfile reproduce un archivo local (vídeo/imagen/audio).
            Console.WriteLine("  playfile <ruta|nom>  reproduce un archivo local (vídeo/imagen/audio)");
            // Ayuda: text muestra un cartel de texto 3D en pantalla.
            Console.WriteLine("  text <texto>         muestra un cartel de texto 3D en pantalla (anuncio)");
            // Ayuda: loadfile verifica un archivo local sin reproducirlo.
            Console.WriteLine("  loadfile <ruta|nom>  verifica un archivo local sin reproducirlo");
            // Ayuda: media lista los medios locales (manifiesto + disco).
            Console.WriteLine("  media                lista los medios locales (manifiesto + disco)");
            // Ayuda: cues lista la cue list recibida del servidor.
            Console.WriteLine("  cues                 lista la cue list recibida del servidor");
            // Ayuda: status muestra estado, motor, mediaRoot y disco libre.
            Console.WriteLine("  status               estado, motor, mediaRoot y disco libre");
            // Ayuda: control del motor (stop, pause, resume, freeze, black).
            Console.WriteLine("  stop|pause|resume|freeze|black  control del motor");
            // Ayuda: fade funde a negro en <s> segundos.
            Console.WriteLine("  fade <s>             fade a negro en <s> segundos");
            // Ayuda: next|prev navega a la siguiente/anterior cue.
            Console.WriteLine("  next|prev            navega a la siguiente/anterior cue");
            // Ayuda: wait espera <s> segundos.
            Console.WriteLine("  wait <s>             espera <s> segundos");
            // Ayuda: exit cierra el cliente.
            Console.WriteLine("  exit                 cierra el cliente");
            break; // Termina este caso.
        // Comando exit: cierra el cliente.
        case "exit":
            Console.WriteLine("Cliente finalizado."); // Mensaje de cierre.
            Environment.Exit(0); // Termina el proceso con código 0.
            break; // Termina este caso.
        // Comando no reconocido.
        default:
            Console.WriteLine("Comando no reconocido"); // Avisa de que el comando no existe.
            break; // Termina este caso.
    }
}

// Resuelve un nombre o ruta de archivo a una ruta absoluta y valida que exista.
static void ResolveLocalFile(StageLnkClient client, string pathOrName, bool loadOnly)
{
    string absolute; // Ruta absoluta que se calculará.
    // Si la ruta indicada ya es absoluta...
    if (Path.IsPathRooted(pathOrName))
    {
        absolute = pathOrName; // Se usa tal cual.
    }
    // Si es solo un nombre o ruta relativa...
    else
    {
        // Busca el medio en la biblioteca local por nombre o ruta relativa.
        var asset = client.ListLocalMedia().FirstOrDefault(m =>
            // Compara ignorando mayúsculas con el nombre del medio.
            string.Equals(m.Name, pathOrName, StringComparison.OrdinalIgnoreCase)
            // Compara ignorando mayúsculas con la ruta relativa del medio.
            || string.Equals(m.RelativePath, pathOrName, StringComparison.OrdinalIgnoreCase));
        // Si no se encuentra el medio en la biblioteca...
        if (asset is null)
        {
            // Avisa de que el archivo no está en la biblioteca local.
            Console.WriteLine($"Archivo local no encontrado: {pathOrName}");
            return; // Sale del método.
        }
        absolute = Path.Combine(client.MediaRoot, asset.RelativePath); // Combina la raíz de medios con la ruta relativa.
    }

    // Comprueba que el archivo exista en disco.
    if (!File.Exists(absolute))
    {
        // Avisa de que la ruta absoluta no existe.
        Console.WriteLine($"Archivo no encontrado: {absolute}");
        return; // Sale del método.
    }

    // Deduce el tipo de medio según la extensión del archivo.
    var kind = MediaAsset.KindForExtension(Path.GetExtension(absolute));
    // Si la extensión no es soportada...
    if (kind is null)
    {
        // Avisa de que el tipo de archivo no está soportado.
        Console.WriteLine($"Tipo de archivo no soportado: {Path.GetExtension(absolute)}");
        return; // Sale del método.
    }

    var info = new FileInfo(absolute); // Obtiene información del archivo.
    // Muestra el resultado de la carga o validación con sus detalles.
    Console.WriteLine($"  {(loadOnly ? "Cargado" : "Listo")}: [{kind}] {Path.GetFileName(absolute)} ({info.Length} bytes) → {absolute}");
}

// Calcula el espacio libre en disco (en MB) de la unidad que contiene la ruta.
static long FreeDiskMb(string root)
{
    // Intenta obtener la información del disco.
    try
    {
        // Obtiene la unidad que contiene la raíz de medios.
        var drive = new DriveInfo(Path.GetPathRoot(root) ?? "C:\\");
        return drive.AvailableFreeSpace / 1024 / 1024; // Convierte los bytes libres a megabytes.
    }
    // Si no se puede leer el disco...
    catch
    {
        return 0; // Devuelve 0 MB.
    }
}

// Construye una barra de espectro con caracteres de bloque de densidad creciente.
static string SpectrumBar(double[] spectrum)
{
    const string levels = " ▁▂▃▄▅▆▇█"; // Niveles visuales, del vacío al bloque lleno.
    // Valor máximo del espectro para normalizar; evita la división por cero.
    double max = spectrum.Length == 0 ? 1 : Math.Max(spectrum.Max(), 1e-9);
    var sb = new System.Text.StringBuilder(spectrum.Length); // Acumulador de la barra.
    // Recorre cada valor de frecuencia del espectro.
    foreach (double value in spectrum)
    {
        // Normaliza el valor a un índice de nivel dentro del rango válido.
        int level = (int)Math.Clamp(value / max * (levels.Length - 1), 0, levels.Length - 1);
        sb.Append(levels[level]); // Añade el carácter del nivel correspondiente.
    }
    return sb.ToString(); // Devuelve la barra construida.
}

/// <summary>
/// Lee la configuración del render desde un JSON: { "monitor": 2, "fullscreen": true }.
/// monitor es 1-based (1 = principal); 0/ausente = principal. Si el archivo no existe
/// o es inválido, devuelve los valores por defecto (monitor principal, ventana).
/// </summary>
// Función que lee la configuración de render desde la ruta indicada.
static (int Monitor, bool Fullscreen) LoadRenderConfig(string path)
{
    // Protege la lectura por si el JSON es inválido.
    try
    {
        // Solo lee el archivo si existe.
        if (File.Exists(path))
        {
            // Analiza el contenido JSON del archivo de configuración.
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            int monitor = 0; // Valor por defecto: monitor principal.
            bool fullscreen = false; // Valor por defecto: sin pantalla completa.
            // Lee la propiedad "monitor" si existe y es un entero.
            if (doc.RootElement.TryGetProperty("monitor", out var m) && m.TryGetInt32(out int mi))
                monitor = mi; // Actualiza el índice de monitor.
            // Lee la propiedad "fullscreen" si existe.
            if (doc.RootElement.TryGetProperty("fullscreen", out var f))
                fullscreen = f.GetBoolean(); // Actualiza el estado de pantalla completa.
            return (monitor, fullscreen); // Devuelve la configuración leída.
        }
    }
    // Si el archivo no existe o es inválido...
    catch
    {
    }
    return (0, false); // Devuelve los valores por defecto en caso de error.
}

// Clase auxiliar para generar medios de demostración.
static class DemoLocal
{
    // Garantiza que existan los medios de demo dentro de la raíz.
    public static void Ensure(string root)
    {
        Directory.CreateDirectory(root); // Crea la carpeta raíz si no existe.

        string tone = Path.Combine(root, "Tono de prueba.wav"); // Ruta del tono de prueba WAV.
        // Si el tono aún no existe...
        if (!File.Exists(tone))
            File.WriteAllBytes(tone, GenerateToneWav(440.0, 3.0)); // Genera un tono de 440 Hz de 3 segundos.

        string video = Path.Combine(root, "Intro video.mp4"); // Ruta del vídeo de introducción.
        // Si el vídeo aún no existe...
        if (!File.Exists(video))
            // Genera un clip de color azul de 2 segundos con ffmpeg.
            RunFfmpeg("-f", "lavfi", "-i", "color=c=0x1a2b4c:s=640x360:d=2", "-pix_fmt", "yuv420p", "-y", video);

        string logo = Path.Combine(root, "Logo.png"); // Ruta del logotipo PNG.
        // Si el logo aún no existe...
        if (!File.Exists(logo))
            // Genera una imagen de color rojo de 1 segundo con ffmpeg.
            RunFfmpeg("-f", "lavfi", "-i", "color=c=0xcc3333:s=640x360:d=1", "-frames:v", "1", "-y", logo);
    }

    // Ejecuta ffmpeg con la lista de argumentos indicada.
    private static void RunFfmpeg(params string[] args)
    {
        // Intenta lanzar ffmpeg.
        try
        {
            // Configura el proceso de ffmpeg.
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false, // Ejecuta el proceso directamente sin shell.
                CreateNoWindow = true, // No abre ninguna ventana de consola.
                RedirectStandardOutput = false, // No redirige la salida estándar.
                RedirectStandardError = false // No redirige la salida de error.
            };
            // Añade cada argumento a la línea de comandos de ffmpeg.
            foreach (string arg in args)
                psi.ArgumentList.Add(arg); // Agrega el argumento al proceso.

            using var proc = System.Diagnostics.Process.Start(psi); // Lanza el proceso de ffmpeg.
            proc?.WaitForExit(30000); // Espera hasta 30 segundos a que termine.
        }
        // Si ffmpeg no está instalado o falla...
        catch
        {
        }
    }

    // Genera un tono WAV PCM de la frecuencia y duración indicadas.
    private static byte[] GenerateToneWav(double frequency, double seconds)
    {
        int sampleRate = 44100; // Frecuencia de muestreo estándar de audio.
        int sampleCount = (int)(sampleRate * seconds); // Número total de muestras del tono.
        short bitsPerSample = 16; // 16 bits por muestra.
        int blockAlign = bitsPerSample / 8; // Bytes por muestra (16 bits = 2 bytes).
        int dataSize = sampleCount * blockAlign; // Tamaño total de los datos de audio.
        int byteRate = sampleRate * blockAlign; // Bytes por segundo de audio.

        using var ms = new MemoryStream(); // Buffer en memoria para el archivo WAV.
        using var w = new BinaryWriter(ms); // Escritor binario sobre el buffer.
        w.Write("RIFF"u8); // Encabezado RIFF del formato WAV.
        w.Write(36 + dataSize); // Tamaño del archivo menos los 8 primeros bytes.
        w.Write("WAVE"u8); // Identificador del formato WAVE.
        w.Write("fmt "u8); // Sub-bloque de formato.
        w.Write(16); // Tamaño del sub-bloque de formato.
        w.Write((short)1); // Tipo de codificación: PCM (1).
        w.Write((short)1); // Número de canales: 1 (mono).
        w.Write(sampleRate); // Frecuencia de muestreo.
        w.Write(byteRate); // Bytes por segundo.
        w.Write((short)blockAlign); // Bytes por bloque de muestra.
        w.Write(bitsPerSample); // Bits por muestra.
        w.Write("data"u8); // Sub-bloque de datos de audio.
        w.Write(dataSize); // Tamaño de los datos de audio.

        double amplitude = short.MaxValue * 0.5; // Amplitud al 50% del máximo para evitar recortes.
        // Genera cada muestra del tono.
        for (int i = 0; i < sampleCount; i++)
        {
            // Fade de entrada y salida para evitar clics al empezar y terminar.
            double envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.05), (sampleCount - i) / (sampleRate * 0.05)));
            // Calcula el valor de la onda senoidal multiplicada por la envolvente.
            double sample = amplitude * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            w.Write((short)sample); // Escribe la muestra convertida a entero corto.
        }

        return ms.ToArray(); // Devuelve el archivo WAV como matriz de bytes.
    }
}
