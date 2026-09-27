// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Visualizer :: ReactiveVisualizer
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Fedo.StageLnk.Client;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
// El tipo Monitor de GLFW choca con System.Threading.Monitor de los usings implícitos.
using MonitorHandle = Silk.NET.GLFW.Monitor;

namespace Fedo.StageLnk.Visualizer; // Espacio de nombres de los visualizadores del proyecto.

/// <summary>
/// Ventana de efecto audio-reactivo en 3D alimentada por el espectro FFT y los
/// beats del cliente (eventos <see cref="BeatInfo"/> y <see cref="SpectrumSnapshot"/>).
/// Renderiza partículas en OpenGL (GLFW) dentro de su propio hilo en segundo plano,
/// de modo que el host de consola puede abrir y cerrar la ventana sin bloquearse.
/// </summary>
// Orquesta la ventana OpenGL 3D y gestiona su ciclo de vida (abrir/cerrar/disponer).
public sealed class ReactiveVisualizer : IDisposable
{
    // Número de bandas del espectro reducido usadas para el color y la posición.
    private const int BandCount = 24;
    // Máximo de partículas vivas que se pueden renderizar a la vez.
    private const int PoolSize = 4000;

    // Candado que sincroniza el hilo de audio y el hilo de render.
    private readonly object _sync = new();
    // Copia local de las bins del espectro reducido.
    private double[] _bins = Array.Empty<double>();
    // Frecuencia de pico actual en Hz.
    private double _peakHz;
    // Fuerza del último beat recibido (aproximada 0..1).
    private double _beat;

    // Título de la ventana del visualizador.
    private readonly string _title;
    // Monitor de destino (bounds) o nulo para usar el principal.
    private readonly DisplayBounds? _monitor;
    // Si se abre en pantalla completa sobre el monitor de destino.
    private readonly bool _fullscreen;
    // Hilo que ejecuta el bucle de render OpenGL.
    private Thread? _thread;
    // Indicador volátil de cierre solicitado por el hilo principal.
    private volatile bool _closing;

    // Icono 32x32 RGBA del programa (gzip+base64) para la ventana GLFW.
    private static readonly byte[] IconBytes = DecompressGz(Convert.FromBase64String(
            "H4sIAAAAAAAEAL2Xd1BVVxrA/Wtnkl1jiwiKDY0VS9QYjSXususSY2HEAFZQQBQs8FBUZAGVAFFhkSZWCBAbVkQpG6WDDSxUFQQEKSpFUJTy7m" +
            "/PfVkJIqzujOud+c1998293+875/vOee926vT74ZTJH5zSJCPryIf+6yLy06wji0s+FHK89REFQU7p0rKNSVLnTm0Op9vSuNUnc1Kmm2+vH/5n" +
            "I3oOGMsnnXt/MOR4Q6bpM8XEvtHiRMZt4ZvUyj1pif+lR+pfTOKTzzT/73TTHMWCXafLbS9Xfi/PueWZ3BtqWhP4tEu/j0YX9eGYH03Pkesxdb" +
            "lj46ddB/CxGTvHgvURhUFDZyzkj920Pjqao3RYf7HobtfeY8T14PemW6/hDBo+ha+mzWlhiPZ0emho/09xZKyjSkr+1H0I74PGgPHo6plgssYF" +
            "Y4UfZvYhrHT4BXOHUIxt/TFZ58ZcQwv6iT5+35gqf49hvIvxU+eydLUj5o5HUOw6j7NvONt9TrXg7HOODbvPs2rbaZGHK5NnzKeL2sh3xlVEPS" +
            "rp/PkIOqKn5jgxplWYKLxZ6xaO895wfgo4icehcLx+vsie4Ci8giNV1zsDwnDxOYZidwQmm/ajv8yavoMndxhbRuXvqU1HfDffjBX2QWz2icL2" +
            "cilrkhtYl9KAW3IFFxKySL5VSGRSNmHR6Rw4mShyicLN7xiOe86IuTqGgYktXdVHdxhf9n+mNpr2GDVxNiY2nmz0OM+m6HxWxyq5WgZn8ySski" +
            "UuFtZTUFFHQXkteY9qiHtQw4GbT/E9ncLOfadx2HOeFXYBfKu7pN34Mopo4e81lrao9f+aJRb/wNLlHK5+J7BJqeNIjkTyU8ioBatYOHxfSVF1" +
            "k6CRq4+bOJgpcaMcdt5qxicsATf/41jvvCj6wZ1B2jpvOWRkfxf1cbRl+t+XYuoQwja/s3gERuCQUoltIuRWw40KsBSfTxQpya1s5l51MycKJT" +
            "KewN3nIi+Rxy/JeXiFRLPD7xQrt51hjqHVWw4ZRXSp8E+gLYamW7Byi8TVPwy/4/HEZJZjf0uJZTKsTQUX4ciuliivlyiolUgoV+J9R+SUA77Z" +
            "Sm7mV7BP9IPr8QS2xJWzIeYBf7Pzo7vm5Dc8sr+rxkRa03vwtyxX7MHaOxbr4DR8T6aSU1jJvYpXxAtPrKDsBbxogtpGcW6GmgZIf6IkvkJJfm" +
            "UD90pq2B9/ny3pDVgnKVkXp8Qm6RULA2LecKn8vSfRGi1tXYxcI1gc3szuFAmD0/XcF/1VVNVAflUzuVVKXirh6UsoFTx+9VsO+c8k8kQtiqob" +
            "yCutxf3Oc5W7UNQkT+QbIObPOrGGgRMNWlyyv1ufb2jNF2PmMH9vFknFEFcK+qeUJOfXqfz3Rb3zapQqX4VwF4u45fXwXMxFYZ3EXZW/UayJOl" +
            "GjBpyviDqJe1JrIKVUwjq5Du1ZVi0ulV9zKq0ZOHIWi0Tfy+PfEa/E8HwjEaVKwosg9rFEpjx+4Rfp8ESMvUrUIF+siZDbEJ4vkSPmP6u4Fq/0" +
            "amxTmokuEM9Ugn1qE2svl6E5Zl6LSxFdVtK973Rao66lg6mtqL9vosi/hsP5SkIywDMejmRBpMghr0aiXuTw8j+kVkj4JEGwyGF7moRfloRLXB" +
            "lOac+wu97MBuG2TqhkipnrGy6Vv98M2jJvyUa2hKbjk9tIULbEqTzwvwLHRX8HPoQrj5UczYSEMnlsSrJFXeLF/O4SayDiAey9Cj+JersfjEBx" +
            "6DqrPU/S90v9tzyyv0f/v9CWr3WW43A0Fc+sZjLFur5ZB8liDhPF/heQj2od7LsB0WLdPxA1LxD7UM7jBpbGiT1I7FGJYh9afrkRJ/8YzLYeRW" +
            "femrccMr/5/0p7bDx4Aa/cJsLuSlSIWleIOu8XY94v6pkhip5bJfa+Z00U1zRSLHq+8Ek9XndesfhXpaCJzZHFbNqfhtWOIHoP/b5dhyK6vOTz" +
            "ATNpj6mLHPHOrMIjU4nnTYndtyW8xN5yrOglGYXV3C+r40HFC/LKnpP9sIaEjDJCL+WyLzITn7NpbAu5wybvC+iaurcbX0YRI/wDdemImYvtcb" +
            "90F9+cevbeeyF6oBA/sR/tDUvl0NlrHIm6SeC566prv2OJeByO5MeASBT/jMV8y2G+M9jQYWwZ2d9Taxat6aNtRP/xK1rQMXBklUMgVu4xOASI" +
            "37agODyCfsUrNB7vo0mqs+fPl9gZGMv2g6ms3R2LpVMwuou2ofWVWUsczdGLaOuyiS4vUB+qR89Bs/8rWmMNWbDCGbPNgaz6MQobsRjtvBNaUH" +
            "jFY+n2L/GfLBSjlS4MmbD4nTFl1l8sOTtypg1qg+e20GfkQrFWjNvlmzl26K1wxWJrIKsdQ1sRjL65O9P0NjFg/PJ2n9UctfgNz8AJxjhce7V+" +
            "muke1L7Qa0F96ALUh//wXmhPNRf1Mny/+4cteMMzTt8Jl+uShsXxnDyNEQb0GjJfhfqwH9AYYfjhEXFfO2SM910tlt//tibXL523/Uydxggj8f" +
            "0C0SfL6DfO7IPTd4yJKr7MzA3B9YpLTza/fge1i6taZxpyJ+9LPWfRq6aiBgYfHDnumNlbMT5wrfi1+99gYJcLABAAAA=="));

    // Descomprime un buffer gzip (icono RGBA incrustado).
    private static byte[] DecompressGz(byte[] data)
    {
        using var input = new MemoryStream(data); // Flujo de entrada del buffer comprimido.
        using var gz = new GZipStream(input, CompressionMode.Decompress); // Descomprime gzip.
        using var output = new MemoryStream(); // Flujo de salida del buffer final.
        gz.CopyTo(output); // Copia el contenido descomprimido.
        return output.ToArray(); // Devuelve los bytes RGBA.
    }
    /// <summary>
    /// Crea el visualizador 3D y arranca su hilo de render en segundo plano.
    /// El audio debe empezar ANTES de llamar a este método.
    /// </summary>
    // Crea el visualizador sin monitor adicional y sin pantalla completa.
    public static ReactiveVisualizer Create(string title)
        => Create(title, null, false); // Sin monitor adicional y sin pantalla completa.

    /// <summary>
    /// Igual que <see cref="Create(string)"/> pero coloca la ventana en el monitor
    /// indicado (bounds de escritorio) en lugar del principal; con <paramref name="fullscreen"/>
    /// abre sin bordes ocupando todo el monitor.
    /// </summary>
    // Variante que permite elegir el monitor de destino (bounds) y el modo pantalla completa.
    public static ReactiveVisualizer Create(string title, DisplayBounds? monitor, bool fullscreen = false)
    {
        var visualizer = new ReactiveVisualizer(title, monitor, fullscreen); // Construye el controlador.
        visualizer.Start(); // Lanza el hilo de render en segundo plano.
        return visualizer; // Devuelve el controlador ya arrancado.
    }

    // Constructor privado que guarda la configuración de la ventana.
    private ReactiveVisualizer(string title, DisplayBounds? monitor, bool fullscreen)
    {
        _title = title; // Guarda el título de la ventana.
        _monitor = monitor; // Guarda el monitor de destino.
        _fullscreen = fullscreen; // Guarda el modo de pantalla completa.
    }

    // Lanza el hilo de render en segundo plano.
    private void Start()
    {
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "FX-OpenGL" }; // Hilo con su nombre.
        _thread.Start(); // Arranca el hilo de render.
    }

    /// <summary>Alimenta el espectro FFT reducido (hilo de audio).</summary>
    // Copia el espectro compartido bajo bloqueo para el hilo de render.
    public void Feed(SpectrumSnapshot spectrum)
    {
        lock (_sync) // Bloqueo de escritura del estado compartido.
        {
            _bins = spectrum.Bins; // Copia las bins del espectro.
            _peakHz = spectrum.PeakFrequencyHz; // Copia la frecuencia de pico.
        }
    }

    /// <summary>Alimenta un beat detectado (hilo de audio).</summary>
    // Copia la fuerza del beat compartida bajo bloqueo.
    public void Feed(BeatInfo beat)
    {
        lock (_sync) // Bloqueo de escritura del beat.
            _beat = beat.Strength; // Guarda la fuerza del beat.
    }

    // Devuelve las últimas bins del espectro bajo bloqueo.
    private double[] LatestBins
    {
        get // Acceso de solo lectura al contenedor de bins.
        {
            lock (_sync) // Bloqueo de lectura.
                return _bins; // Devuelve la referencia actual de bins.
        }
    }

    // Devuelve la última fuerza de beat bajo bloqueo.
    private double LatestBeat
    {
        get // Acceso de solo lectura a la fuerza del beat.
        {
            lock (_sync) // Bloqueo de lectura.
                return _beat; // Devuelve la fuerza del beat.
        }
    }

    /// <summary>Cierra la ventana y detiene el hilo de render.</summary>
    // Solicita el cierre del bucle y espera a que el hilo termine.
    public void Close()
    {
        _closing = true; // Solicita el cierre del bucle de render.
        if (_thread is not null && _thread.IsAlive) // Si el hilo sigue vivo...
            _thread.Join(2000); // ...espera hasta dos segundos a que termine.
    }

    // Al disponer el visualizador se cierra la ventana.
    public void Dispose() => Close(); // Delega en Close.

    // -------------------------------------------------------------------------
    // Bucle de render OpenGL (se ejecuta en el hilo secundario).
    // -------------------------------------------------------------------------

    // Inicializa GLFW, crea la ventana, carga OpenGL y ejecuta el bucle de frames.
    private void RenderLoop()
    {
        Glfw glfw = Glfw.GetApi(); // Punto de entrada de la API GLFW.
        try
        {
            if (!glfw.Init()) // Si GLFW no se puede inicializar...
            {
                Console.Error.WriteLine("[FX3D] No se pudo inicializar GLFW."); // Avisa por stderr.
                return; // Sale sin abrir la ventana.
            }
            unsafe // Las llamadas a GLFW y a OpenGL usan punteros nativos.
            {
                // Crea la ventana (ya colocada en el monitor de destino).
                WindowHandle* win = CreateGlfwWindow(glfw);
                if (win is null) // Si no se pudo crear la ventana...
                {
                    glfw.Terminate(); // Termina la biblioteca GLFW.
                    return; // Sale sin renderizar.
                }
                glfw.MakeContextCurrent(win); // Activa el contexto OpenGL de la ventana.
                glfw.SwapInterval(1); // Activa el VSync para un refresco suave.
                // Carga las funciones de OpenGL a través de GLFW.
                GL gl = GL.GetApi(name => glfw.GetProcAddress(name));
                RenderScene(glfw, gl, win); // Configura el render y ejecuta el bucle de frames.
                glfw.DestroyWindow(win); // Destruye la ventana al salir del bucle.
            }
            glfw.Terminate(); // Termina la biblioteca GLFW.
        }
        catch (Exception ex) // Cualquier fallo del render no debe tumbar al host.
        {
            Console.Error.WriteLine($"[FX3D] Error de render: {ex.Message}"); // Registra el fallo.
            try { glfw.Terminate(); } catch { /* La biblioteca puede ya estar cerrada. */ }
        }
    }

    // Crea la ventana GLFW, centrada en el monitor de destino si se indicó.
    private unsafe WindowHandle* CreateGlfwWindow(Glfw glfw)
    {
        int w = _monitor?.Width ?? 960; // Ancho inicial de la ventana.
        int h = _monitor?.Height ?? 540; // Alto inicial de la ventana.
        // Crea la ventana con su contexto OpenGL (visible de inmediato).
        WindowHandle* win = glfw.CreateWindow(w, h, _title, null, null);
        if (win is null) // Si falló la creación...
            return null; // Devuelve nulo.
        // Fija el icono del programa en la ventana (32x32 RGBA descomprimido).
        fixed (byte* px = IconBytes)
        {
            var image = new Silk.NET.GLFW.Image { Width = 32, Height = 32, Pixels = px }; // Imagen del icono.
            glfw.SetWindowIcon(win, 1, &image); // Aplica el icono a la ventana.
        }
        if (_monitor is DisplayBounds mb) // Si hay monitor de destino...
        {
            // Busca el monitor GLFW cuyas coordenadas coinciden con las del objetivo.
            MonitorHandle* target = FindMonitor(glfw, mb);
            if (target is not null) // Si se encontró el monitor...
            {
                glfw.GetMonitorPos(target, out int mx, out int my); // Lee su posición.
                VideoMode* mode = glfw.GetVideoMode(target); // Lee su modo de vídeo.
                if (_fullscreen) // Pantalla completa "borderless" (sin modo exclusivo).
                {
                    // Las ventanas sin marco no se componen en este equipo, así que se
                    // usa una ventana con marco pero se saca el marco fuera de pantalla.
                    glfw.GetWindowFrameSize(win, out int fl, out int ft, out int fr, out int fb);
                    glfw.SetWindowPos(win, mx - fl, my - ft); // Cliente en el origen del monitor.
                    glfw.SetWindowSize(win, mode->Width, mode->Height); // Cliente del tamaño del monitor.
                }
                else // Si no, centra la ventana sobre el monitor de destino.
                    glfw.SetWindowPos(win, mx + (mode->Width - w) / 2, my + (mode->Height - h) / 2);
            }
        }
        return win; // Devuelve la ventana creada.
    }

    // Busca el monitor GLFW cuyas coordenadas coinciden con los bounds del escritorio.
    private unsafe MonitorHandle* FindMonitor(Glfw glfw, DisplayBounds mb)
    {
        // Obtiene el array de monitores de GLFW y su número.
        MonitorHandle** monitors = glfw.GetMonitors(out int count);
        for (int i = 0; i < count; i++) // Recorre todos los monitores...
        {
            glfw.GetMonitorPos(monitors[i], out int x, out int y); // Lee la posición.
            VideoMode* mode = glfw.GetVideoMode(monitors[i]); // Lee el modo de vídeo.
            if (x == mb.X && y == mb.Y // Si coincide la posición...
                && mode->Width == mb.Width && mode->Height == mb.Height) // ...y el tamaño...
                return monitors[i]; // Devuelve este monitor.
        }
        return null; // Sin coincidencia: se usará el monitor principal.
    }

    // Configura los shaders y buffers, y ejecuta el bucle principal de frames.
    private unsafe void RenderScene(Glfw glfw, GL gl, WindowHandle* win)
    {
        using var renderer = new Renderer(gl); // Compila los shaders y crea los buffers.
        var clock = Stopwatch.StartNew(); // Reloj de alta resolución para el delta de tiempo.
        float last = 0; // Última marca de tiempo del frame.
        float lastSwap = 0; // Instante del último SwapBuffers (para limitar el ritmo).
        const float frameInterval = 1f / 60f; // Objetivo de ~60 fps.
        while (!glfw.WindowShouldClose(win) && !_closing) // Mientras no se cierre la ventana...
        {
            glfw.PollEvents(); // Procesa los eventos de la ventana.
            float now = (float)clock.Elapsed.TotalSeconds; // Tiempo actual del frame.
            float dt = Math.Clamp(now - last, 0f, 0.1f); // Delta acotado (evita saltos).
            last = now; // Actualiza la marca de tiempo.
            UpdateSim(dt); // Simula el audio y las partículas.
            glfw.GetFramebufferSize(win, out int fbw, out int fbh); // Tamaño real en píxeles.
            if (fbw < 2 || fbh < 2) // Tamaño inválido...
                continue; // ...salta este frame.
            DrawFrame(gl, renderer, fbw, fbh); // Dibuja el frame en OpenGL.
            glfw.SwapBuffers(win); // Presenta el frame en pantalla.
            // Limita el ritmo a ~60 fps porque el VSync no se aplica en este equipo.
            float frameMs = (float)clock.Elapsed.TotalMilliseconds - lastSwap;
            if (frameMs < frameInterval * 1000f)
                Thread.Sleep((int)(frameInterval * 1000f - frameMs)); // Descansa lo que falta.
            lastSwap = (float)clock.Elapsed.TotalMilliseconds; // Marca el instante de presentación.
        }
    }

    // -------------------------------------------------------------------------
    // Simulación audio-reactiva de las partículas 3D.
    // -------------------------------------------------------------------------

    // Pool de partículas 3D (structs, sin asignaciones por frame).
    private readonly Particle[] _pool = new Particle[PoolSize];
    // Búfer de vértices reutilizado para subir las partículas a la GPU.
    private readonly Vertex[] _verts = new Vertex[PoolSize];
    // Número de partículas vivas al inicio del pool.
    private int _count;
    // Energía suavizada por banda (ataque rápido, caída lenta).
    private readonly double[] _smooth = new double[BandCount];
    // Nivel de normalización que decae lento (evita el parpadeo).
    private double _norm = 1e-6;
    // Energía media del espectro suavizada.
    private double _avg;
    // Energía de graves suavizada para tamaño, cámara y gravedad.
    private double _bass;
    // Pulso de beat local que decae en cada frame.
    private double _beatPulse;
    // Última fuerza de beat que ya disparó una ráfaga (flanco ascendente).
    private double _burstThreshold;
    // Fracción acumulada de partículas ambientales pendientes de nacer.
    private double _spawnAcc;
    // Reloj de la animación en segundos.
    private double _time;
    // Ángulo de órbita de la cámara alrededor de la escena.
    private float _camAngle;

    // Simula el audio y las partículas un paso de tiempo.
    private void UpdateSim(float dt)
    {
        double[] bins = LatestBins; // Recupera las últimas bins del espectro.
        double beat = LatestBeat; // Recupera la última fuerza de beat.
        int n = Math.Min(bins.Length, BandCount); // Bandas efectivas a usar.

        // Busca el máximo del espectro para normalizar las bandas.
        double maxv = 0;
        for (int i = 0; i < n; i++) // Recorre las bandas del espectro.
            maxv = Math.Max(maxv, bins[i]); // Actualiza el máximo.
        if (maxv <= 0) // Evita la normalización por cero.
            maxv = 1e-6; // Usa un mínimo de seguridad.
        // Nivel que sube al instante y decae lento (evita el parpadeo).
        _norm = Math.Max(maxv, _norm * 0.985);
        if (_norm <= 0) // Si el nivel se anuló por error...
            _norm = 1e-6; // Restaura un mínimo de seguridad.

        // Suaviza cada banda y acumula la energía media y las graves.
        double sum = 0; // Acumulador de la energía media.
        double bassLow = 0; // Máximo de las bandas graves.
        for (int i = 0; i < n; i++)
        {
            double target = Math.Clamp(bins[i] / _norm, 0, 1); // Valor normalizado de la banda.
            double rate = target > _smooth[i] ? 0.5 : 0.12; // Coeficiente de subida/bajada.
            _smooth[i] += (target - _smooth[i]) * rate; // Interpola hacia el objetivo.
            sum += _smooth[i]; // Acumula para calcular la media.
            if (i < 4 && _smooth[i] > bassLow) // Solo las bandas más graves...
                bassLow = _smooth[i]; // ...para alimentar graves, cámara y gravedad.
        }
        _avg = n > 0 ? sum / n : 0; // Energía media del espectro.
        _bass = Math.Max(bassLow, _bass * 0.94); // Graves suavizadas con decaimiento.

        // Flanco ascendente del beat: lanza una ráfaga esférica de partículas.
        if (beat > 0.25 && beat > _burstThreshold + 0.05)
        {
            Burst((float)beat); // Dispara la ráfaga con la fuerza del beat.
            _burstThreshold = beat; // Recuerda esta fuerza para no repetir el estallido.
        }
        // Pulso que sube con el beat y decae, para cámara y brillo.
        _beatPulse = Math.Min(1, _beatPulse * 0.9 + Math.Max(0, beat - 0.25) * 0.8);

        // Nace partículas ambientales a una tasa proporcional a la energía.
        _spawnAcc += (12 + 260 * (0.15 + _avg * 1.6)) * dt;
        int toSpawn = (int)_spawnAcc; // Número entero de partículas a nacer ahora.
        _spawnAcc -= toSpawn; // Conserva la fracción para el próximo frame.
        for (int k = 0; k < toSpawn && _count < PoolSize; k++) // Nace cada partícula...
            SpawnAmbient(); // Crea una partícula ambiental más.

        // Gravedad que aumenta con la energía de graves (la música "pesa").
        float gravity = (float)(-3.4 - 2.2 * _bass);
        for (int i = 0; i < _count; i++) // Recorre el pool vivo...
        {
            ref Particle p = ref _pool[i]; // Referencia a la partícula actual.
            p.VY += gravity * dt; // Aplica la gravedad.
            p.VX *= 0.992f; // Amortigua la velocidad horizontal.
            p.VY *= 0.992f; // Amortigua la velocidad vertical.
            p.VZ *= 0.992f; // Amortigua la velocidad de profundidad.
            p.X += p.VX * dt; // Avanza la posición en X.
            p.Y += p.VY * dt; // Avanza la posición en Y.
            p.Z += p.VZ * dt; // Avanza la posición en Z.
            p.Life -= dt; // Consume la vida restante.
        }

        // Compacta el pool descartando las partículas muertas o fuera de escena.
        int write = 0; // Posición de escritura durante la compactación.
        for (int r = 0; r < _count; r++) // Recorre todas las partículas vivas.
        {
            Particle p = _pool[r]; // Partícula evaluada.
            if (p.Life > 0 && p.Y > -2 && p.Y < 14 // Si sigue viva y dentro de límites...
                && Math.Abs(p.X) < 22 && Math.Abs(p.Z) < 22) // ...en todo el espacio...
                _pool[write++] = p; // La adelanta al frente del pool.
        }
        _count = write; // Actualiza el número de partículas vivas.

        _time += dt; // Avanza el reloj de la animación.
        // La cámara orbita más rápido con graves y beats.
        _camAngle += dt * (0.14f + 0.35f * (float)_bass + 1.2f * (float)_beatPulse);
    }

    // Crea una partícula ambiental en el suelo, posicionada por su banda.
    private void SpawnAmbient()
    {
        int b = WeightedBand(); // Banda elegida ponderando por su energía.
        double v = _smooth[b]; // Energía suavizada de la banda elegida.
        double f = b / (double)(BandCount - 1); // Fracción de banda para color y posición.
        // Posición en el suelo: graves a la izquierda, agudos a la derecha.
        float x = (float)(-5.0 + f * 10.0 + (Random.Shared.NextDouble() - 0.5) * 1.6);
        float z = (float)((Random.Shared.NextDouble() - 0.5) * 8.0); // Profundidad aleatoria.
        float y = 0.05f; // Nace justo sobre el suelo.
        // Velocidad ascendente con algo de deriva lateral y de profundidad.
        float vx = (float)((Random.Shared.NextDouble() - 0.5) * 1.4);
        float vy = (float)(0.8 + Random.Shared.NextDouble() * 1.3 + v * 2.4);
        float vz = (float)((Random.Shared.NextDouble() - 0.5) * 1.4);
        // Vida y tamaño según la banda (los graves son más grandes).
        float life = (float)(1.6 + Random.Shared.NextDouble() * 2.2);
        float size = (float)(3.0 + v * 9.0 + (b < 4 ? 3.5 : 0));
        // Color HSV según la banda (rojo grave → azul agudo), saturado.
        (float r, float g, float bl) = Hsv(f, 0.9, 0.85);
        Add(x, y, z, vx, vy, vz, life, life, r, g, bl, size); // Añade la partícula.
    }

    // Lanza una ráfaga esférica de partículas cuando suena un beat fuerte.
    private void Burst(float strength)
    {
        int burst = (int)(150 + 320 * strength); // Cuenta proporcional a la fuerza.
        for (int k = 0; k < burst && _count < PoolSize; k++) // Nace cada partícula...
        {
            // Dirección aleatoria uniforme sobre la esfera unitaria.
            float theta = (float)(Random.Shared.NextDouble() * Math.PI * 2);
            float phi = (float)Math.Acos(2 * Random.Shared.NextDouble() - 1);
            // Rapidez de la ráfaga según la fuerza del beat.
            float speed = (float)(2.4 + 3.8 * strength) * (0.5f + 0.5f * (float)Random.Shared.NextDouble());
            float vx = MathF.Sin(phi) * MathF.Cos(theta) * speed; // Velocidad en X.
            float vy = MathF.Cos(phi) * speed * 1.15f; // Velocidad en Y (ligera alzada).
            float vz = MathF.Sin(phi) * MathF.Sin(theta) * speed; // Velocidad en Z.
            // Vida corta y vibrante típica de un cohete de fuegos.
            float life = (float)(0.6 + 1.2 * Random.Shared.NextDouble());
            (float r, float g, float b) = Hsv((float)Random.Shared.NextDouble(), 1f, 1f); // Color del arco iris.
            float size = (float)(5 + 11 * strength * (0.6 + Random.Shared.NextDouble() * 0.8)); // Tamaño grande.
            Add(0, 1.6f, 0, vx, vy, vz, life, life, r, g, b, size); // Añade la partícula al centro.
        }
    }

    // Elige una banda ponderando por su energía suavizada.
    private int WeightedBand()
    {
        double total = 0; // Suma de las energías de todas las bandas.
        for (int i = 0; i < BandCount; i++) // Recorre las bandas acumulando energía.
            total += _smooth[i];
        if (total <= 0) // Energía nula: no hay preferencia.
            return Random.Shared.Next(BandCount); // Elige una banda al azar.
        double r = Random.Shared.NextDouble() * total; // Punto de corte aleatorio.
        double acc = 0; // Energía acumulada mientras se recorre.
        for (int i = 0; i < BandCount; i++) // Recorre bandas acumulando hasta el corte.
        {
            acc += _smooth[i]; // Suma la energía de la banda actual.
            if (r <= acc) // Si el corte cae en esta banda...
                return i; // Devuelve esta banda.
        }
        return BandCount - 1; // Fallback: la última banda.
    }

    // Añade una partícula nueva al pool si queda hueco.
    private void Add(float x, float y, float z, float vx, float vy, float vz,
        float life, float maxLife, float r, float g, float b, float size)
    {
        if (_count >= PoolSize) // Pool lleno: no cabe otra partícula.
            return; // Descarta el nacimiento.
        _pool[_count] = new Particle // Escribe la partícula en el siguiente hueco.
        {
            X = x, Y = y, Z = z, VX = vx, VY = vy, VZ = vz, // Posición y velocidad.
            Life = life, MaxLife = maxLife, // Vida actual y total.
            R = r, G = g, B = b, Size = size // Color y tamaño.
        };
        _count++; // Registra la nueva partícula viva.
    }

    // Copia las partículas vivas al búfer de vértices con brillo por vida.
    private void BakeVerts()
    {
        for (int i = 0; i < _count; i++) // Recorre las partículas vivas...
        {
            ref Particle p = ref _pool[i]; // Referencia a la partícula actual.
            float fade = p.Life / p.MaxLife; // Brillo proporcional a la vida restante.
            _verts[i] = new Vertex // Escribe el vértice con color atenuado.
            {
                X = p.X, Y = p.Y, Z = p.Z, // Posición 3D.
                R = p.R * fade, G = p.G * fade, B = p.B * fade, // Color con atenuación.
                Size = p.Size // Tamaño del punto.
            };
        }
    }

    // Convierte un color HSV a RGB en coma flotante 0..1.
    private static (float R, float G, float B) Hsv(double hue, double sat, double val)
    {
        int i = (int)Math.Floor(hue * 6); // Sector del cilindro HSV (0..5).
        double f = hue * 6 - i; // Fracción dentro del sector.
        double p = val * (1 - sat); // Componente auxiliar (valor - croma).
        double q = val * (1 - f * sat); // Componente auxiliar intermedia.
        double t = val * (1 - (1 - f) * sat); // Componente auxiliar intermedia.
        // Selecciona el trío RGB según el sector del color.
        (double r, double g, double b) = i switch
        {
            0 => (val, t, p), // Sector 0: rojo → amarillo.
            1 => (q, val, p), // Sector 1: amarillo → verde.
            2 => (p, val, t), // Sector 2: verde → cian.
            3 => (p, q, val), // Sector 3: cian → azul.
            4 => (t, p, val), // Sector 4: azul → magenta.
            _ => (val, p, q) // Sector 5 (o fuera de rango): magenta → rojo.
        }; // Fin de la expresión switch.
        // Convierte los componentes a floats con saturación.
        return ((float)r, (float)g, (float)b);
    }

    // -------------------------------------------------------------------------
    // Dibujo OpenGL del frame.
    // -------------------------------------------------------------------------

    // Dibuja el suelo de rejilla y las partículas con mezcla aditiva.
    private unsafe void DrawFrame(GL gl, Renderer renderer, int fbw, int fbh)
    {
        gl.Viewport(0, 0, (uint)fbw, (uint)fbh); // Ajusta el área de dibujo al tamaño real.
        gl.ClearColor(0.015f, 0.02f, 0.045f, 1f); // Fondo azul muy oscuro.
        gl.Clear(ClearBufferMask.ColorBufferBit); // Limpia el búfer de color.
        gl.Enable(EnableCap.Blend); // Activa la mezcla de colores.
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // Mezcla aditiva (brillo).
        gl.Enable(EnableCap.ProgramPointSize); // Permite fijar el tamaño del punto en shaders.

        // Matriz de proyección en perspectiva.
        float aspect = fbw / (float)Math.Max(1, fbh); // Relación de aspecto.
        var proj = Matrix4X4.CreatePerspectiveFieldOfView(55f * MathF.PI / 180f, aspect, 0.1f, 100f);
        // Cámara en órbita: se acerca y eleva con las graves.
        float radius = 6.2f - 0.6f * (float)_bass;
        float camX = MathF.Sin(_camAngle) * radius; // X de la cámara.
        float camZ = MathF.Cos(_camAngle) * radius; // Z de la cámara.
        var view = Matrix4X4.CreateLookAt(
            new Vector3D<float>(camX, 2.5f + 0.6f * (float)_bass, camZ), // Posición de la cámara.
            new Vector3D<float>(0, 1.6f, 0), // Punto al que mira (centro de la escena).
            Vector3D<float>.UnitY); // Vector "arriba" del mundo.
        Matrix4X4<float> mvp = view * proj; // Vista × proyección (convención row-major de Silk.NET.Maths).
        // Escala de puntos: más grandes en pantallas de alta resolución.
        float pointScale = fbh / 540f;

        gl.UseProgram(renderer.Program); // Activa el programa de shaders.
        gl.UniformMatrix4(renderer.MvpLoc, 1, false, (float*)&mvp); // Sube la matriz MVP.
        gl.Uniform1(renderer.ScaleLoc, pointScale); // Sube la escala de puntos.
        gl.Uniform2(renderer.ViewportLoc, (float)fbw, (float)fbh); // Sube el tamaño del viewport.

        // Dibuja la rejilla del suelo (líneas) como referencia 3D.
        gl.Uniform1(renderer.IsPointLoc, 0); // La rejilla se dibuja sólida.
        gl.BindVertexArray(renderer.GridVao); // Activa los atributos de la rejilla.
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)renderer.GridVertexCount); // Dibuja las líneas.

        // Dibuja las partículas como puntos con brillo aditivo.
        gl.Uniform1(renderer.IsPointLoc, 1); // Las partículas usan disco suave.
        BakeVerts(); // Rellena el búfer de vértices desde el pool.
        gl.BindVertexArray(renderer.Vao); // Activa los atributos de las partículas.
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, renderer.Vbo); // Activa el buffer de partículas.
        // Sube los vértices actuales a la GPU (flujo dinámico por frame).
        gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, _verts.AsSpan(0, _count), BufferUsageARB.StreamDraw);
        gl.DrawArrays(PrimitiveType.Points, 0, (uint)_count); // Dibuja las partículas.

        gl.BindVertexArray(0); // Suelta el VAO activo.
    }

    // Partícula individual de la simulación (struct mutable, sin asignaciones por frame).
    private struct Particle
    {
        public float X, Y, Z; // Posición 3D en el mundo.
        public float VX, VY, VZ; // Velocidad 3D en unidades por segundo.
        public float Life, MaxLife; // Vida restante y vida total.
        public float R, G, B; // Color RGB en 0..1.
        public float Size; // Tamaño del punto en píxeles (aproximado).
    }

    // Vértice enviado a la GPU (layout secuencial: pos, color, tamaño).
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public float X, Y, Z; // Posición 3D.
        public float R, G, B; // Color RGB.
        public float Size; // Tamaño del punto.
    }

    // Gestor de recursos OpenGL: shaders, VAOs y VBOs del visualizador.
    private sealed class Renderer : IDisposable
    {
        private readonly GL _gl; // API de OpenGL.
        private bool _disposed; // Si los recursos ya se liberaron.

        public uint Program { get; } // Programa de shaders (partículas + rejilla).
        public int MvpLoc { get; } // Ubicación del uniform de la matriz MVP.
        public int ScaleLoc { get; } // Ubicación del uniform de escala de puntos.
        public int IsPointLoc { get; } // Ubicación del uniform de modo punto/línea.
        public int ViewportLoc { get; } // Ubicación del uniform de tamaño del viewport.
        public uint Vao { get; } // VAO de las partículas.
        public uint Vbo { get; } // VBO de las partículas.
        public uint GridVao { get; } // VAO de la rejilla del suelo.
        public uint GridVbo { get; } // VBO de la rejilla del suelo.
        public int GridVertexCount { get; } // Número de vértices de la rejilla.

        // Compila el programa y crea los buffers de partículas y rejilla.
        public Renderer(GL gl)
        {
            _gl = gl; // Guarda la API OpenGL.
            Program = CompileProgram(gl); // Compila el programa de shaders.
            MvpLoc = gl.GetUniformLocation(Program, "uMVP"); // Localiza el uniform MVP.
            ScaleLoc = gl.GetUniformLocation(Program, "uPointScale"); // Localiza la escala.
            IsPointLoc = gl.GetUniformLocation(Program, "uIsPoint"); // Localiza el modo punto.
            ViewportLoc = gl.GetUniformLocation(Program, "uViewport"); // Localiza el viewport.
            (Vao, Vbo) = CreateParticleBuffer(gl); // Crea los buffers de las partículas.
            (GridVao, GridVbo, GridVertexCount) = CreateGrid(gl); // Crea los buffers de la rejilla.
        }

        // Crea el VAO y el VBO con los atributos de las partículas.
        private static unsafe (uint vao, uint vbo) CreateParticleBuffer(GL gl)
        {
            uint vao = gl.GenVertexArray(); // Genera el VAO.
            uint vbo = gl.GenBuffer(); // Genera el VBO.
            gl.BindVertexArray(vao); // Activa el VAO.
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo); // Activa el VBO.
            const uint stride = 28; // Tamaño del vértice (7 floats × 4 bytes).
            gl.EnableVertexAttribArray(0); // Habilita el atributo de posición.
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1); // Habilita el atributo de color.
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
            gl.EnableVertexAttribArray(2); // Habilita el atributo de tamaño.
            gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)24);
            gl.BindVertexArray(0); // Suelta el VAO.
            return (vao, vbo); // Devuelve los identificadores.
        }

        // Crea el VAO y el VBO con las líneas de la rejilla del suelo.
        private static unsafe (uint vao, uint vbo, int count) CreateGrid(GL gl)
        {
            var verts = new List<Vertex>(); // Lista de vértices de la rejilla.
            const float range = 6f; // Mitad del tamaño de la rejilla.
            // Color de la rejilla (azul tenue).
            float r = 0.10f, g = 0.22f, b = 0.42f;
            for (float z = -range; z <= range + 0.001f; z += 1f) // Líneas paralelas al eje X...
            {
                verts.Add(new Vertex { X = -range, Y = 0, Z = z, R = r, G = g, B = b, Size = 1 });
                verts.Add(new Vertex { X = range, Y = 0, Z = z, R = r, G = g, B = b, Size = 1 });
            }
            for (float x = -range; x <= range + 0.001f; x += 1f) // Líneas paralelas al eje Z...
            {
                verts.Add(new Vertex { X = x, Y = 0, Z = -range, R = r, G = g, B = b, Size = 1 });
                verts.Add(new Vertex { X = x, Y = 0, Z = range, R = r, G = g, B = b, Size = 1 });
            }
            uint vao = gl.GenVertexArray(); // Genera el VAO de la rejilla.
            uint vbo = gl.GenBuffer(); // Genera el VBO de la rejilla.
            gl.BindVertexArray(vao); // Activa el VAO.
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo); // Activa el VBO.
            const uint stride = 28; // Mismo layout que las partículas.
            gl.EnableVertexAttribArray(0); // Habilita el atributo de posición.
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
            gl.EnableVertexAttribArray(1); // Habilita el atributo de color.
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);
            gl.EnableVertexAttribArray(2); // Habilita el atributo de tamaño.
            gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)24);
            gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, verts.ToArray(), BufferUsageARB.StaticDraw); // Sube la rejilla.
            gl.BindVertexArray(0); // Suelta el VAO.
            return (vao, vbo, verts.Count); // Devuelve buffers y número de vértices.
        }

        // Compila el programa de shaders y comprueba los errores.
        private static uint CompileProgram(GL gl)
        {
            uint vs = CompileShader(gl, ShaderType.VertexShader, VertexSrc); // Compila el vertex.
            uint fs = CompileShader(gl, ShaderType.FragmentShader, FragmentSrc); // Compila el fragment.
            uint prog = gl.CreateProgram(); // Crea el programa.
            gl.AttachShader(prog, vs); // Adjunta el vertex shader.
            gl.AttachShader(prog, fs); // Adjunta el fragment shader.
            gl.LinkProgram(prog); // Enlaza el programa.
            gl.DeleteShader(vs); // Libera el vertex shader ya enlazado.
            gl.DeleteShader(fs); // Libera el fragment shader ya enlazado.
            string log = gl.GetProgramInfoLog(prog); // Lee el log del enlace.
            if (!string.IsNullOrWhiteSpace(log)) // Si hay avisos o errores...
                Console.Error.WriteLine($"[FX3D] Enlace de programa: {log}"); // ...los muestra.
            return prog; // Devuelve el programa listo.
        }

        // Compila un shader individual y comprueba los errores.
        private static uint CompileShader(GL gl, ShaderType type, string src)
        {
            uint shader = gl.CreateShader(type); // Crea el shader.
            gl.ShaderSource(shader, src); // Carga el código fuente.
            gl.CompileShader(shader); // Compila el shader.
            string log = gl.GetShaderInfoLog(shader); // Lee el log de compilación.
            if (!string.IsNullOrWhiteSpace(log)) // Si hay avisos o errores...
                Console.Error.WriteLine($"[FX3D] Shader {type}: {log}"); // ...los muestra.
            return shader; // Devuelve el shader compilado.
        }

        // Código del vertex shader: transforma posición y fija el tamaño del punto.
        // vClip (posición en clip space) y vSize pasan al fragment para calcular
        // el disco suave sin usar gl_PointCoord (no fiable en este driver).
        private const string VertexSrc = """
            #version 330 core
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec3 aColor;
            layout(location = 2) in float aSize;
            uniform mat4 uMVP;
            uniform float uPointScale;
            out vec3 vColor;
            out vec4 vClip;
            out float vSize;
            void main() {
                gl_Position = uMVP * vec4(aPos, 1.0);
                gl_PointSize = aSize * uPointScale;
                vColor = aColor;
                vClip = gl_Position;
                vSize = gl_PointSize;
            }
            """;

        // Código del fragment shader: la rejilla (líneas) va sólida y el punto
        // se recorta a un disco suave midiendo la distancia al centro del punto
        // en píxeles (gl_FragCoord). Se selecciona con uIsPoint.
        private const string FragmentSrc = """
            #version 330 core
            in vec3 vColor;
            in vec4 vClip;
            in float vSize;
            uniform int uIsPoint;
            uniform vec2 uViewport;
            out vec4 fragColor;
            void main() {
                float a = 1.0;
                if (uIsPoint == 1) {
                    vec3 ndc = vClip.xyz / vClip.w;
                    vec2 center = (ndc.xy * 0.5 + 0.5) * uViewport;
                    float d = distance(gl_FragCoord.xy, center);
                    float radius = max(vSize * 0.5, 1.0);
                    a = clamp(1.0 - d / radius, 0.0, 1.0);
                    if (a <= 0.0) discard;
                }
                fragColor = vec4(vColor * a, a);
            }
            """;

        // Libera los recursos de OpenGL.
        public void Dispose()
        {
            if (_disposed) // Ya se liberaron antes...
                return; // No hay nada que hacer.
            _disposed = true; // Marca como liberado.
            _gl.DeleteVertexArray(Vao); // Elimina el VAO de partículas.
            _gl.DeleteBuffer(Vbo); // Elimina el VBO de partículas.
            _gl.DeleteVertexArray(GridVao); // Elimina el VAO de la rejilla.
            _gl.DeleteBuffer(GridVbo); // Elimina el VBO de la rejilla.
            _gl.DeleteProgram(Program); // Elimina el programa de shaders.
        }
    }
}
