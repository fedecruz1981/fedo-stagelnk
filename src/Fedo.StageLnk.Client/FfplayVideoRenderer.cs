// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: FfplayVideoRenderer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics; // Proporciona Process y ProcessStartInfo para lanzar ffplay
using System.Globalization; // Formatea números con cultura invariable (p. ej. el offset de -ss)
using Fedo.StageLnk.Shared; // Herramientas externas compartidas (ffmpeg/ffprobe/ffplay)

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>
/// Renderer de vídeo/imagen real usando ffplay (ventana SDL propia).
/// Pause finaliza el proceso registrando la posición reproducida; Resume
/// relanza ffplay con `-ss &lt;posición&gt;` para continuar desde donde se quedó.
/// </summary>
public sealed class FfplayVideoRenderer : IDisposable
{
    public event Action<string>? Log; // Evento de log para informar de las acciones del renderer

    private readonly object _lock = new(); // Candado que serializa el acceso al estado interno
    private Process? _process; // Proceso ffplay en ejecución (null si no hay ninguno)
    private DateTimeOffset? _startedUtc; // Momento en que arrancó ffplay (para calcular la posición)
    private double _accumulatedSeconds; // Segundos acumulados de reproducción antes de la última pausa
    private bool _paused; // Indica si la reproducción está en pausa
    private string? _path; // Ruta absoluta del archivo que se está reproduciendo
    private string? _title; // Título de ventana configurado para ffplay
    private bool _loop; // Indica si el vídeo/imagen debe repetirse en bucle

    /// <summary>
    /// Monitor en el que abrir la ventana (bounds de escritorio). Si es null, ffplay
    /// abre en el monitor principal por defecto.
    /// </summary>
    public DisplayBounds? TargetMonitor { get; set; } // Monitor destino de la ventana (null = principal)

    /// <summary>Si es true, la ventana de vídeo/imagen se abre a pantalla completa.</summary>
    public bool Fullscreen { get; set; } // Habilita o deshabilita el modo pantalla completa

    public double MeasuredFps
    {
        get
        {
            lock (_lock) // Protege la lectura del estado compartido
                return _process is not null && !_process.HasExited ? 30.0 : 0; // 30 FPS si ffplay está vivo; 0 si está detenido
        }
    }

    public bool IsActive
    {
        get
        {
            lock (_lock) // Protege la lectura del proceso
                return _process is not null && !_process.HasExited; // Hay actividad si el proceso existe y no ha terminado
        }
    }

    /// <summary>Indica si ffplay está disponible en el sistema.</summary>
    public bool IsAvailable // Disponibilidad de ffplay en el sistema
    {
        get => ExternalTools.IsFfplayAvailable; // Delega en el resolvedor compartido
    }

    public bool IsPaused
    {
        get
        {
            lock (_lock) // Protege la lectura del flag de pausa
                return _paused; // Devuelve si la reproducción está en pausa
        }
    }

    public double PositionSeconds
    {
        get
        {
            lock (_lock) // Protege el cálculo de la posición
                return PositionSecondsUnsafe(); // Devuelve la posición en segundos calculada bajo candado
        }
    }

    private double PositionSecondsUnsafe()
    {
        double elapsed = _accumulatedSeconds; // Parte de los segundos ya acumulados
        if (_startedUtc is not null && _process is { HasExited: false }) // Solo suma si el proceso sigue corriendo
            elapsed += (DateTimeOffset.UtcNow - _startedUtc.Value).TotalSeconds; // Añade el tiempo transcurrido desde el arranque
        return elapsed; // Devuelve la posición total estimada
    }

    public void Play(string absolutePath, string? windowTitle = null, bool paused = false, bool loop = false)
    {
        Stop(); // Detiene cualquier reproducción previa antes de empezar

        // Detecta si la entrada es una URL de streaming (ffplay acepta URLs directamente)
        bool isUrl = absolutePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || absolutePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isUrl && !File.Exists(absolutePath)) // Solo exige existencia en disco si no es una URL
        {
            Log?.Invoke($"Render: archivo no encontrado ({absolutePath})"); // Notifica que el archivo no existe
            return; // Aborta sin abrir ventana
        }

        _path = absolutePath; // Guarda la ruta del archivo a reproducir
        _title = windowTitle; // Guarda el título de ventana solicitado
        _loop = loop; // Guarda si la reproducción debe ser en bucle

        try
        {
            var ffplayPath = ExternalTools.FfplayPath ?? "ffplay";
            if (!ExternalTools.IsFfplayAvailable)
            {
                Log?.Invoke("Render: ffplay no está disponible en el sistema (variable FEDO_FFPLAY o PATH)");
                return;
            }

            // Prepara el proceso ffplay
            var psi = new ProcessStartInfo(ffplayPath)
            {
                // No pasa por el shell del sistema
                UseShellExecute = false,
                // No crea ventana de consola para ffplay
                CreateNoWindow = true
            };
            // Añade el flag para salir automáticamente al terminar el archivo
            psi.ArgumentList.Add("-autoexit");
            // Añade el flag para eliminar los bordes de la ventana SDL
            psi.ArgumentList.Add("-noborder");
            // Añade el flag para que la ventana se mantenga siempre por encima
            psi.ArgumentList.Add("-alwaysontop");
            if (loop) // Si se pidió reproducción en bucle
            {
                // ffplay exige un valor numérico en -loop (0 = bucle infinito).
                // Un `-loop` desnudo se interpreta mal (espera un número) y ffplay
                // aborta el arranque sin abrir ventana.
                // Añade el flag de bucle
                psi.ArgumentList.Add("-loop");
                // Valor 0 = bucle infinito (ffplay exige el número)
                psi.ArgumentList.Add("0");
            }
            if (paused) // Si se debe iniciar en pausa
                // Añade el flag de inicio en pausa
                psi.ArgumentList.Add("-paused");
            if (!string.IsNullOrWhiteSpace(windowTitle)) // Si se proporcionó título de ventana
            {
                // Añade el flag de título de ventana
                psi.ArgumentList.Add("-window_title");
                // Añade el título en sí
                psi.ArgumentList.Add(windowTitle);
            }
            AddMonitorArgs(psi); // Añade los argumentos de posición de ventana y pantalla completa
            // Añade el flag de archivo de entrada
            psi.ArgumentList.Add("-i");
            // Añade la ruta del archivo como entrada
            psi.ArgumentList.Add(absolutePath);

            _process = Process.Start(psi); // Lanza ffplay con los argumentos preparados
            _startedUtc = DateTimeOffset.UtcNow; // Registra el momento de arranque
            _accumulatedSeconds = 0; // Reinicia la posición acumulada
            _paused = paused; // Registra el estado de pausa solicitado
            Log?.Invoke($"Render: ffplay abierto para '{Path.GetFileName(absolutePath)}'"); // Notifica la apertura del vídeo
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Render: no se pudo lanzar ffplay ({ex.Message})"); // Notifica el fallo al lanzar ffplay
            _process = null; // Limpia la referencia al proceso fallido
            _startedUtc = null; // Limpia el momento de arranque
            _paused = false; // Resetea el estado de pausa
        }
    }

    public void Pause()
    {
        lock (_lock) // Bloquea el estado para pausar de forma atómica
        {
            if (_process is null || _process.HasExited) // Si no hay proceso o ya terminó
            {
                _accumulatedSeconds = 0; // Reinicia la posición acumulada
                _paused = false; // Desactiva el flag de pausa
                return; // Sale sin hacer nada más
            }

            _accumulatedSeconds = PositionSecondsUnsafe(); // Congela la posición alcanzada hasta ahora
            KillProcess(); // Mata el proceso ffplay para congelar la imagen
            _paused = true; // Marca la reproducción como pausada
        }
        Log?.Invoke($"Render: PAUSA (posición {_accumulatedSeconds:F1}s)"); // Notifica la pausa con su posición
    }

    public void Resume()
    {
        double offset; // Posición desde la que se reanudará
        lock (_lock) // Bloquea el estado para reanudar de forma atómica
        {
            if (!_paused) // Si no está en pausa, no hay nada que reanudar
                return; // Sale sin actuar

            _paused = false; // Limpia el flag de pausa
            offset = _accumulatedSeconds; // Recupera la posición guardada en la pausa
            _accumulatedSeconds = 0; // Reinicia el acumulador
            if (_path is not null) // Solo relanza si hay un archivo conocido
                Relaunch(offset); // Relanza ffplay desde la posición guardada
        }
        Log?.Invoke($"Render: REANUDAR desde {offset:F1}s"); // Notifica la reanudación con su offset
    }

    private void Relaunch(double offsetSeconds)
    {
        try
        {
            var ffplayPath = ExternalTools.FfplayPath ?? "ffplay";
            if (!ExternalTools.IsFfplayAvailable)
            {
                Log?.Invoke("Render: ffplay no está disponible en el sistema (variable FEDO_FFPLAY o PATH)");
                return;
            }

            // Prepara un nuevo proceso ffplay
            var psi = new ProcessStartInfo(ffplayPath)
            {
                // No pasa por el shell del sistema
                UseShellExecute = false,
                // No crea ventana de consola
                CreateNoWindow = true
            };
            // Añade el flag de salida automática al terminar
            psi.ArgumentList.Add("-autoexit");
            // Añade el flag para eliminar los bordes de la ventana
            psi.ArgumentList.Add("-noborder");
            // Añade el flag para que la ventana se mantenga siempre por encima
            psi.ArgumentList.Add("-alwaysontop");
            if (_loop) // Si el medio debe repetirse en bucle
            {
                // Mismo requisito que en Play(): -loop debe llevar valor (0 = infinito).
                // Añade el flag de bucle
                psi.ArgumentList.Add("-loop");
                // Valor 0 = bucle infinito
                psi.ArgumentList.Add("0");
            }
            if (offsetSeconds > 0.5) // Solo aplica el desplazamiento si es relevante
            {
                // Añade el flag de seek a la posición
                psi.ArgumentList.Add("-ss");
                // Añade la posición formateada con cultura invariable
                psi.ArgumentList.Add(offsetSeconds.ToString("0.00", CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrWhiteSpace(_title)) // Si hay título de ventana guardado
            {
                // Añade el flag de título de ventana
                psi.ArgumentList.Add("-window_title");
                // Añade el título guardado
                psi.ArgumentList.Add(_title);
            }
            AddMonitorArgs(psi); // Añade la posición de ventana y pantalla completa
            // Añade el flag de archivo de entrada
            psi.ArgumentList.Add("-i");
            // Añade la ruta del archivo (no nula en este punto)
            psi.ArgumentList.Add(_path!);

            _process = Process.Start(psi); // Relanza ffplay con la nueva configuración
            _startedUtc = DateTimeOffset.UtcNow; // Reinicia el contador de tiempo de arranque
            Log?.Invoke($"Render: ffplay reanudado para '{Path.GetFileName(_path!)}'"); // Notifica la reanudación
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Render: no se pudo relanzar ffplay ({ex.Message})"); // Notifica el fallo al relanzar ffplay
            _process = null; // Limpia la referencia al proceso fallido
            _startedUtc = null; // Limpia el momento de arranque
        }
    }

    public void Stop()
    {
        lock (_lock) // Bloquea el estado para detener de forma atómica
        {
            KillProcess(); // Finaliza el proceso ffplay si existe
            _accumulatedSeconds = 0; // Reinicia la posición acumulada
            _paused = false; // Desactiva el flag de pausa
            _path = null; // Olvida la ruta actual
            _title = null; // Olvida el título actual
            _loop = false; // Desactiva el bucle
        }
        Log?.Invoke("Render: vídeo detenido"); // Notifica la detención del vídeo
    }

    private void AddMonitorArgs(ProcessStartInfo psi)
    {
        DisplayBounds? monitor; // Monitor destino a aplicar
        bool fullscreen; // Flag local de pantalla completa
        lock (_lock) // Bloquea para leer la configuración compartida
        {
            monitor = TargetMonitor; // Lee el monitor configurado
            fullscreen = Fullscreen; // Lee el flag de pantalla completa
        }
        if (fullscreen) // Si está activado el modo completo
        {
            // Añade el flag de pantalla completa de ffplay
            psi.ArgumentList.Add("-fs");
            Log?.Invoke("Render: modo pantalla completa"); // Notifica el modo pantalla completa
        }
        if (monitor is null) // Sin monitor explícito no hay más argumentos que añadir
            return; // Sale de la función
        // Añade el flag de posición X de la ventana
        psi.ArgumentList.Add("-left");
        // Añade la coordenada X del monitor
        psi.ArgumentList.Add(monitor.Value.X.ToString(CultureInfo.InvariantCulture));
        // Añade el flag de posición Y de la ventana
        psi.ArgumentList.Add("-top");
        // Añade la coordenada Y del monitor
        psi.ArgumentList.Add(monitor.Value.Y.ToString(CultureInfo.InvariantCulture));
        // Añade el flag de ancho de ventana
        psi.ArgumentList.Add("-x");
        // Añade el ancho del monitor
        psi.ArgumentList.Add(monitor.Value.Width.ToString(CultureInfo.InvariantCulture));
        // Añade el flag de alto de ventana
        psi.ArgumentList.Add("-y");
        // Añade el alto del monitor
        psi.ArgumentList.Add(monitor.Value.Height.ToString(CultureInfo.InvariantCulture));
        Log?.Invoke($"Render: ventana en monitor ({monitor.Value.Width}x{monitor.Value.Height}) en ({monitor.Value.X},{monitor.Value.Y})"); // Notifica la posición final de la ventana
    }

    private void KillProcess()
    {
        if (_process is not null) // Solo actúa si hay un proceso registrado
        {
            try
            {
                if (!_process.HasExited) // Evita matar un proceso ya terminado
                {
                    _process.Kill(entireProcessTree: true); // Termina ffplay y todo su árbol de procesos
                    _process.WaitForExit(2000); // Espera hasta 2 s a que el proceso acabe
                }
            }
            catch
            {
                // Ignora errores al finalizar el proceso
            }
            _process.Dispose(); // Libera los recursos del proceso
            _process = null; // Suelta la referencia al proceso
        }
        _startedUtc = null; // Invalida el momento de arranque
    }

    public void Dispose() => Stop(); // Al liberar el objeto, detiene la reproducción
}
