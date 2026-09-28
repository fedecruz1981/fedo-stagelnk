// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server.Host :: Program (host de consola del servidor)
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using Fedo.StageLnk.Protocol;
using Fedo.StageLnk.Server;
using Fedo.StageLnk.Shared;

// Banner de presentación del servidor definido como cadena bruta multilínea
const string Banner = """
    ╔══════════════════════════════════════════════╗
    ║         Fedo-StageLnk  SERVER  v1.0          ║
    ║   Professional Network Media Server           ║
    ╚══════════════════════════════════════════════╝
    """; // cierre del banner

Console.OutputEncoding = System.Text.Encoding.UTF8; // la consola usa UTF-8 para mostrar el banner correctamente
Console.WriteLine(Banner); // imprime el banner de bienvenida

// Diagnóstico de herramientas externas
var (ffmpeg, ffprobe, ffplay) = ExternalTools.CheckAll();
Console.WriteLine($"[Diag] ffmpeg: {(ffmpeg ? "OK" : "NO ENCONTRADO")}  ffprobe: {(ffprobe ? "OK" : "NO ENCONTRADO")}  ffplay: {(ffplay ? "OK" : "NO ENCONTRADO")}");
if (!ffmpeg || !ffprobe || !ffplay)
    Console.WriteLine("Advertencia: faltan herramientas de FFmpeg. Instale FFmpeg y añádalo al PATH, o defina FEDO_FFMPEG/FEDO_FFPROBE/FEDO_FFPLAY.");
Console.WriteLine();

// Directorio raíz de la biblioteca: primer argumento sin guion, o la ruta predeterminada si no se indica ninguno
string libraryRoot = args.FirstOrDefault(a => !a.StartsWith("-")) ?? Path.Combine(Environment.CurrentDirectory, "Library", "Server");
// Si se pasa el argumento --demo se activa el modo demostración
bool demo = args.Contains("--demo");

if (demo) // si estamos en modo demo...
    DemoLibrary.Ensure(libraryRoot); // ...se crea la biblioteca de prueba si no existe

await using var server = new StageLnkServer(libraryRoot); // crea y reserva (dispose automático) el servidor de medios
// Suscriptor de log: imprime cada mensaje del servidor con marca de tiempo
server.Log += m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
// Suscriptor de conexión: avisa por consola cuando un cliente se conecta
server.ClientConnected += s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] + Cliente online: {s.Name} [{s.Id}]");
// Suscriptor de desconexión: avisa por consola cuando un cliente se desconecta
server.ClientDisconnected += id => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] - Cliente offline: {id}");
// Suscriptor de estado: muestra la telemetría (CPU, GPU, FPS, RAM...) de cada cliente
server.ClientStatusUpdated += (s, r) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   {s.Name}: {r.Status} | CPU={r.CpuUsage:F0}% GPU={r.GpuUsage:F0}% FPS={r.Fps:F0} RAM={r.RamUsedMb}MB disco={r.FreeDiskMb}MB cue={r.LastCueNumber} temp={(r.TemperatureC > 0 ? $"{r.TemperatureC:F0}°C" : "-")}");
// Suscriptor de listo: avisa cuando un cliente termina de sincronizar y está listo
server.ClientReady += s => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s.Name} está READY");

server.Start(); // inicia el servidor
await server.ScanLibraryAsync(); // escanea los medios de la biblioteca

if (demo) // en modo demo...
    DemoLibrary.InstallCues(server); // ...se instalan las cues de ejemplo

Console.WriteLine(); // línea en blanco de separación
// Muestra la ayuda de comandos de reproducción
Console.WriteLine("Comandos: play <n> | stop | pause | resume | next | prev | freeze | black | fade <s> | current");
// Muestra la ayuda de comandos de texto, cues y capas
Console.WriteLine("          text <texto> | playto <cliente> <n> | addcue <n> <kind> <nombre> | delcue <n> | layer <n> <z> <media>");
// Muestra la ayuda de comandos de proyectos, guardado y consultas
Console.WriteLine("          saveproj <file.fsl> | loadproj | savecues <file.fslcue> | loadcues | cues | media | clients | exit");
Console.WriteLine(); // línea en blanco de separación

int cmdDelayMs = 0; // retardo opcional (en milisegundos) antes de ejecutar los comandos por línea de comandos
// Busca el argumento cmd-delay:N que indica los milisegundos de espera inicial
var delayArg = args.FirstOrDefault(a => a.TrimStart('-').StartsWith("cmd-delay:", StringComparison.OrdinalIgnoreCase));
if (delayArg is not null) // si se indicó un retardo...
// ...se intenta parsear el número que sigue a "cmd-delay:"
    _ = int.TryParse(delayArg.TrimStart('-')[10..], out cmdDelayMs);

// Lista de comandos extraídos de los argumentos de la línea de comandos
var commands = args
    .Where(a => a.TrimStart('-').StartsWith("cmd:", StringComparison.OrdinalIgnoreCase)) // filtra los argumentos que empiezan por "cmd:"
    .Select(a => a.TrimStart('-')[4..]) // quita el prefijo "cmd:" para quedarse con el comando puro
    .ToList(); // convierte el resultado en una lista
// Busca el argumento cmds-file:<ruta> para leer comandos desde un archivo de texto
var cmdsFile = args.FirstOrDefault(a => a.TrimStart('-').StartsWith("cmds-file:", StringComparison.OrdinalIgnoreCase))?.TrimStart('-')[10..];
if (cmdsFile is not null && File.Exists(cmdsFile)) // si se indicó un archivo y existe...
// ...añade cada línea no vacía del archivo como comando
    commands.AddRange(File.ReadAllLines(cmdsFile).Where(l => !string.IsNullOrWhiteSpace(l)));

if (commands.Count > 0 && cmdDelayMs > 0) // si hay comandos automáticos y un retardo configurado...
    await Task.Delay(cmdDelayMs); // ...espera el retardo antes de ejecutarlos

foreach (string cmd in commands) // recorre los comandos automáticos
{
// Muestra el comando que se va a ejecutar
    Console.WriteLine($"> {cmd}");
    await ExecuteCommandAsync(server, cmd); // ejecuta el comando
}

while (true) // bucle principal de la consola interactiva
{
    string? line = Console.ReadLine(); // lee una línea escrita por el operador
    if (line is null) // si el flujo de entrada cerró (fin de stdin)...
    {
        await Task.Delay(200); // ...espera 200 ms antes de reintentar
        continue; // ...y vuelve a intentar leer una línea
    }
    await ExecuteCommandAsync(server, line); // ejecuta el comando introducido
}

static async Task ExecuteCommandAsync(StageLnkServer server, string line) // despacha y ejecuta un comando de texto
{
// Divide el comando en palabras separadas por espacios (ignorando vacíos y recortando espacios)
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) return; // si no hay palabras, no hay nada que hacer

    switch (parts[0].ToLowerInvariant()) // despacha según la primera palabra en minúsculas
    {
// play <n>: reproduce la cue número n
        case "play" when parts.Length > 1 && int.TryParse(parts[1], out int n):
            await server.PlayCueAsync(n); // reproduce la cue solicitada
            break; // sale del switch
// playto <cliente> <n>: reproduce la cue n en un cliente concreto
        case "playto" when parts.Length >= 3 && int.TryParse(parts[2], out int toNum):
            await server.PlayCueToAsync(parts[1], toNum); // envía la cue al cliente indicado
            break; // sale del switch
// stop: detiene la reproducción actual
        case "stop": await server.StopAsync(); break;
// pause: pausa la reproducción actual
        case "pause": await server.PauseAsync(); break;
// resume: reanuda la reproducción pausada
        case "resume": await server.ResumeAsync(); break;
// next: pasa a la cue siguiente
        case "next": await server.NextAsync(); break;
// prev: vuelve a la cue anterior
        case "prev": await server.PreviousAsync(); break;
// freeze: congela la imagen actual
        case "freeze": await server.FreezeAsync(); break;
// black: apaga la salida a negro
        case "black": await server.BlackOutAsync(); break;
// fade <s>: fundido a negro en s segundos
        case "fade" when parts.Length > 1 && double.TryParse(parts[1], out double s):
            await server.FadeAsync(s, toBlack: true); // ejecuta el fundido a negro
            break; // sale del switch
// text <texto>: envía un texto a todos los clientes
        case "text" when parts.Length > 1:
// Une el resto de palabras para reconstruir el texto completo
            await server.BroadcastTextAsync(string.Join(' ', parts[1..]));
            break; // sale del switch
// current: muestra el número de la cue actual
        case "current":
// Imprime el número de la cue actual con tres dígitos
            Console.WriteLine($"Cue actual: {server.CurrentCueNumber:000}");
            break; // sale del switch
// addcue <n> <kind> <nombre>: crea o actualiza una cue
        case "addcue" when parts.Length >= 3 && int.TryParse(parts[1], out int cueNum) && Enum.TryParse<CueKind>(parts[2], true, out var cueKind):
// Crea la cue (o la actualiza) con los datos introducidos
            server.UpsertCue(new Cue
            {
                Number = cueNum, // número de la cue
// Nombre: el texto restante, o "Cue n" si no se indicó nombre
                Name = parts.Length > 3 ? string.Join(' ', parts[3..]) : $"Cue {cueNum}",
                Kind = cueKind // tipo de cue (video, audio, imagen...)
            });
            await server.BroadcastCueListAsync(); // notifica la nueva lista de cues a los clientes
            break; // sale del switch
// delcue <n>: elimina la cue número n
        case "delcue" when parts.Length > 1 && int.TryParse(parts[1], out int delNum):
            server.Cues.Remove(delNum); // elimina la cue de la colección
            await server.BroadcastCueListAsync(); // notifica la nueva lista de cues
            break; // sale del switch
// layer <n> <z> <media>: añade una capa con profundidad z a la cue n
        case "layer" when parts.Length >= 4 && int.TryParse(parts[1], out int cueNum) && int.TryParse(parts[2], out int z):
// Añade la capa usando el texto restante como nombre del medio
            AddLayer(server, cueNum, z, string.Join(' ', parts[3..]));
            await server.BroadcastCueListAsync(); // notifica la nueva lista de cues
            break; // sale del switch
// saveproj <archivo.fsl>: guarda el proyecto actual
        case "saveproj" when parts.Length > 1:
            await server.SaveProjectAsync(parts[1]); // guarda el proyecto en el archivo indicado
            break; // sale del switch
// loadproj <archivo.fsl>: carga un proyecto
        case "loadproj" when parts.Length > 1:
            await server.LoadProjectAsync(parts[1]); // carga el proyecto desde el archivo indicado
            break; // sale del switch
// savecues <archivo.fslcue>: guarda la biblioteca de cues
        case "savecues" when parts.Length > 1:
            await server.SaveCueLibraryAsync(parts[1]); // guarda las cues en el archivo indicado
            break; // sale del switch
// loadcues <archivo.fslcue>: carga una biblioteca de cues
        case "loadcues" when parts.Length > 1:
            await server.LoadCueLibraryAsync(parts[1]); // carga las cues desde el archivo indicado
            break; // sale del switch
// cues: lista todas las cues de la biblioteca
        case "cues":
            foreach (var cue in server.Cues.Cues) // recorre cada cue de la biblioteca
            {
// Concatena las capas de la cue como texto (profundidad z y los primeros 8 caracteres del id de medio)
                string layers = string.Join(", ", cue.Layers.Select(l => $"z{l.ZOrder}:{l.MediaId?[..Math.Min(8, l.MediaId.Length)]}"));
// Imprime la cue formateada con su número, nombre, tipo y capas
                Console.WriteLine($"  {cue.Number:000} {cue.Name,-20} [{cue.Kind}]{(layers.Length > 0 ? "  " + layers : "")}");
            }
            break; // sale del switch
// media: lista todos los medios de la biblioteca
        case "media":
            foreach (var m in server.Library.All) // recorre cada medio de la biblioteca
            {
// Dimensiones: anchoxalto, o "-" si el medio no tiene tamaño
                string dim = m.Width > 0 ? $"{m.Width}x{m.Height}" : "-";
// Imprime el medio con id corto, tipo, tamaño, duración, dimensiones y ruta relativa
                Console.WriteLine($"  {m.Id[..8]}  {m.Kind,-8} {m.Size,12}  dur={m.DurationSeconds,4}s  {dim,-10} {m.RelativePath}");
            }
// Muestra el total de medios en la biblioteca
            Console.WriteLine($"  Total: {server.Library.All.Count}");
            break; // sale del switch
// clients: muestra el estado de todos los clientes conectados
        case "clients":
// Cabecera de la tabla de clientes
            Console.WriteLine($"  {"CLIENTE",-16} {"ESTADO",-8} {"CPU%",-5} {"GPU",-22} {"GPU%",-5} {"FPS",-5} {"RAM",-8} {"RES",-11} {"TEMP",-6} {"CUE",-4} {"SYNC",-10}");
            foreach (var c in server.Clients) // recorre cada cliente conectado
            {
                var r = c.LastReport; // último informe de telemetría del cliente
// Nombre de la GPU, truncado a 20 caracteres si es más largo, o "-" si no hay informe
                string gpu = r is { } && r.Gpu.Length > 0 ? (r.Gpu.Length > 20 ? r.Gpu[..20] : r.Gpu) : "-";
// Resolución: anchoxalto, o "-" si no se ha informado
                string res = r is { } && r.ResolutionWidth > 0 ? $"{r.ResolutionWidth}x{r.ResolutionHeight}" : "-";
// Temperatura en grados Celsius, o "-" si no se informa
                string temp = r is { } && r.TemperatureC > 0 ? $"{r.TemperatureC:F0}°C" : "-";
// RAM usada/total en MB, o "-" si no hay informe
                string ram = r is { } ? $"{r.RamUsedMb}/{r.RamTotalMb}MB" : "-";
// Sincronización: estado de sync si está listo, o el número de medios pendientes de sincronizar
                string sync = c.ReadyForShow ? (r?.SyncState ?? "ok") : $"{c.SyncedMediaIds.Count} medios";
// Estado del cliente: el del informe, o Ready/Connecting según su preparación
                var status = r?.Status ?? (c.ReadyForShow ? ClientStatus.Ready : ClientStatus.Connecting);
// Fila de la tabla con todos los datos del cliente
                Console.WriteLine($"  {c.Name,-16} {status,-8} {(r is null ? "-" : $"{r.CpuUsage:F0}"),-5} {gpu,-22} {(r is null ? "-" : $"{r.GpuUsage:F0}"),-5} {(r is null ? "-" : $"{r.Fps:F0}"),-5} {ram,-8} {res,-11} {temp,-6} {r?.LastCueNumber ?? 0,-4} {sync,-10}");
            }
// Muestra el total de clientes conectados
            Console.WriteLine($"  Total: {server.Clients.Count} clientes");
            break; // sale del switch
// exit: cierra el proceso del servidor
        case "exit": Environment.Exit(0); break;
        default: // cualquier comando no reconocido
// Mensaje para comandos desconocidos
            Console.WriteLine("Comando no reconocido");
            break; // sale del switch
    }
}

static void AddLayer(StageLnkServer server, int cueNumber, int z, string mediaName) // añade una capa de medio a una cue
{
    var cue = server.Cues.Find(cueNumber); // busca la cue por su número
    if (cue is null) // si la cue no existe...
    {
// Avisa de que la cue no existe
        Console.WriteLine($"Cue {cueNumber:000} no existe");
        return; // ...y termina
    }

// Busca el medio por su nombre (ignorando mayúsculas/minúsculas)
    var media = server.Library.All.FirstOrDefault(m =>
        string.Equals(m.Name, mediaName, StringComparison.OrdinalIgnoreCase));
    if (media is null) // si el medio no se encuentra...
    {
// Avisa de que el medio no está en la biblioteca
        Console.WriteLine($"Medio '{mediaName}' no encontrado en la biblioteca");
        return; // ...y termina
    }

    var layer = new LayerSpec { ZOrder = z, MediaId = media.Id }; // crea la capa con su profundidad y el id del medio
    var updated = cue with { Layers = cue.Layers.Append(layer).ToList() }; // cue con la nueva capa añadida al final
    server.UpsertCue(updated); // guarda la cue actualizada
// Confirma por consola la capa añadida
    Console.WriteLine($"Capa z={z} -> {media.Name} añadida al cue {cueNumber:000}");
}

static class DemoLibrary // biblioteca de demostración para el modo --demo
{
    public static void Ensure(string root) // crea la biblioteca de prueba con audio, vídeo e imagen
    {
        Directory.CreateDirectory(root); // asegura que existe el directorio raíz

// Ruta del tono de prueba WAV (440 Hz, 3 segundos)
        string tone = Path.Combine(root, "Tono de prueba.wav");
        if (!File.Exists(tone)) // si el tono no existe...
            File.WriteAllBytes(tone, GenerateToneWav(440.0, 3.0)); // ...lo genera y lo escribe en disco

// Ruta del vídeo de introducción MP4
        string video = Path.Combine(root, "Intro video.mp4");
        if (!File.Exists(video)) // si el vídeo no existe...
// ...lo genera con ffmpeg (filtro lavfi: color azul 640x360, 2 segundos)
            RunFfmpeg("-f", "lavfi", "-i", "color=c=0x1a2b4c:s=640x360:d=2", "-pix_fmt", "yuv420p", "-y", video);

// Ruta del logo PNG
        string logo = Path.Combine(root, "Logo.png");
        if (!File.Exists(logo)) // si el logo no existe...
// ...lo genera con ffmpeg (filtro lavfi: color rojo 640x360, un fotograma)
            RunFfmpeg("-f", "lavfi", "-i", "color=c=0xcc3333:s=640x360:d=1", "-frames:v", "1", "-y", logo);
    }

    private static void RunFfmpeg(params string[] args) // ejecuta ffmpeg con los argumentos dados
    {
        try // intenta lanzar ffmpeg...
        {
// Configuración del proceso de ffmpeg sin ventana ni salidas redirigidas
            var psi = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false, // no usa el shell del sistema
                CreateNoWindow = true, // no crea ventana de consola
                RedirectStandardOutput = false, // no captura la salida estándar
                RedirectStandardError = false // no captura el error estándar
            };
            foreach (string arg in args) // añade cada argumento...
                psi.ArgumentList.Add(arg); // ...a la lista de argumentos del proceso

            using var proc = System.Diagnostics.Process.Start(psi); // lanza ffmpeg
            proc?.WaitForExit(30000); // espera hasta 30 segundos a que termine
        }
        catch // si ffmpeg no está disponible...
        {
            // ffmpeg no disponible: se continúa con la biblioteca de audio solamente
        }
    }

    public static void InstallCues(StageLnkServer server) // instala las cues de demostración en el servidor
    {
// Busca el id del tono en la biblioteca
        string? toneId = server.Library.All.FirstOrDefault(m => m.Name.Contains("Tono"))?.Id;
// Busca el id del vídeo de introducción en la biblioteca
        string? videoId = server.Library.All.FirstOrDefault(m => m.Name.Contains("Intro video"))?.Id;
// Busca el id del logo en la biblioteca
        string? logoId = server.Library.All.FirstOrDefault(m => m.Name == "Logo")?.Id;

// Cue 1: "Intro", de tipo vídeo, con fundidos y una capa con el vídeo
        server.UpsertCue(new Cue
        {
            Number = 1, // número de la cue
// Nombre de la cue
            Name = "Intro",
            Kind = CueKind.Video, // tipo: vídeo
            FadeIn = 1.0, // fundido de entrada de 1 segundo
            FadeOut = 1.0, // fundido de salida de 1 segundo
// Capas: lista vacía si no hay vídeo, o una capa con el vídeo a opacidad total
            Layers = videoId is null
// Sin vídeo: sin capas
                ? new List<LayerSpec>()
// Con vídeo: una capa en z=1 con el medio y opacidad 1.0
                : new List<LayerSpec> { new LayerSpec { ZOrder = 1, MediaId = videoId, Opacity = 1.0 } }
        });
// Cue 2: "Tono 440Hz", de tipo audio, con una capa con el tono
        server.UpsertCue(new Cue
        {
            Number = 2, // número de la cue
// Nombre de la cue
            Name = "Tono 440Hz",
            Kind = CueKind.Audio, // tipo: audio
            Duration = 3.0, // duración de 3 segundos
// Capa única con el tono en z=1 y opacidad total
            Layers = { new LayerSpec { ZOrder = 1, MediaId = toneId, Opacity = 1.0 } }
        });
// Cue 3: "Logo", de tipo imagen, con una capa con el logo
        server.UpsertCue(new Cue
        {
            Number = 3, // número de la cue
// Nombre de la cue
            Name = "Logo",
            Kind = CueKind.Image, // tipo: imagen
// Capas: lista vacía si no hay logo, o una capa con el logo
            Layers = logoId is null
// Sin logo: sin capas
                ? new List<LayerSpec>()
// Con logo: una capa en z=1 con el medio y opacidad 1.0
                : new List<LayerSpec> { new LayerSpec { ZOrder = 1, MediaId = logoId, Opacity = 1.0 } }
        });
// Cue 4: "Freeze", de tipo congelar imagen, con fundido de entrada
        server.UpsertCue(new Cue
        {
            Number = 4, // número de la cue
// Nombre de la cue
            Name = "Freeze",
            Kind = CueKind.Freeze, // tipo: congelar imagen
            FadeIn = 0.5 // fundido de entrada de medio segundo
        });
// Cue 5: "Black", de tipo salida a negro
        server.UpsertCue(new Cue
        {
            Number = 5, // número de la cue
// Nombre de la cue
            Name = "Black",
            Kind = CueKind.Black // tipo: salida a negro
        });
// Cue 6: "Final", de tipo fundido cruzado, para terminar el espectáculo
        server.UpsertCue(new Cue
        {
            Number = 6, // número de la cue
// Nombre de la cue
            Name = "Final",
            Kind = CueKind.Crossfade, // tipo: fundido cruzado
            FadeIn = 2.0, // fundido de entrada de 2 segundos
            FadeOut = 2.0 // fundido de salida de 2 segundos
        });
    }

    private static byte[] GenerateToneWav(double frequency, double seconds) // genera un WAV con un tono senoidal
    {
        int sampleRate = 44100; // frecuencia de muestreo: 44100 Hz
        int sampleCount = (int)(sampleRate * seconds); // número total de muestras
        short bitsPerSample = 16; // 16 bits por muestra
        int blockAlign = bitsPerSample / 8; // bytes por muestra: 2
        int dataSize = sampleCount * blockAlign; // tamaño en bytes de los datos de audio
        int byteRate = sampleRate * blockAlign; // bytes por segundo

        using var ms = new MemoryStream(); // flujo en memoria para construir el WAV
        using var w = new BinaryWriter(ms); // escritor binario sobre el flujo
// Cabecera RIFF: marca de tipo "RIFF"
        w.Write("RIFF"u8);
        w.Write(36 + dataSize); // tamaño total del archivo menos los 8 primeros bytes
// Formato WAVE
        w.Write("WAVE"u8);
// Sub-chunk "fmt " con la descripción del formato
        w.Write("fmt "u8);
        w.Write(16); // tamaño del sub-chunk fmt: 16 bytes
        w.Write((short)1); // formato PCM sin compresión
        w.Write((short)1); // 1 canal (mono)
        w.Write(sampleRate); // frecuencia de muestreo
        w.Write(byteRate); // bytes por segundo
        w.Write((short)blockAlign); // bytes por muestra
        w.Write(bitsPerSample); // bits por muestra
// Sub-chunk "data": los datos de audio
        w.Write("data"u8);
        w.Write(dataSize); // tamaño de los datos de audio

        double amplitude = short.MaxValue * 0.5; // amplitud al 50% del máximo para evitar recortes
        for (int i = 0; i < sampleCount; i++) // recorre cada muestra de audio
        {
// Envolvente: subida/bajada progresiva (5% del inicio y del final) para evitar clics
            double envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.05), (sampleCount - i) / (sampleRate * 0.05)));
// Muestra senoidal: amplitud * envolvente * seno(2π * frecuencia * tiempo)
            double sample = amplitude * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate);
            w.Write((short)sample); // escribe la muestra como entero de 16 bits
        }

        return ms.ToArray(); // devuelve el WAV completo como un arreglo de bytes
    }
}
