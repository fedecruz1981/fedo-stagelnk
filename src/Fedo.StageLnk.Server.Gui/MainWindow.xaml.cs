// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server.Gui :: MainWindow
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Collections.ObjectModel; // colecciones observables que notifican cambios a la UI
using System.ComponentModel; // interfaces como INotifyPropertyChanged
using System.Diagnostics; // Process y ProcessStartInfo para lanzar ffmpeg
using System.IO; // operaciones de archivos y rutas (File, Path, Directory)
using System.Runtime.CompilerServices; // atributo [CallerMemberName] para las propiedades
using System.Windows; // tipos base de WPF (Window, RoutedEventArgs, MessageBox)
using System.Windows.Controls; // controles de WPF (ComboBox, TextBox, etc.)
using System.Windows.Threading; // Dispatcher para sincronizar con el hilo de la UI
using Fedo.StageLnk.Protocol; // tipos del protocolo (Cue, CueKind, LayerSpec, etc.)
using Fedo.StageLnk.Server; // StageLnkServer y clases del servidor
using Fedo.StageLnk.Shared; // Herramientas externas compartidas (ffmpeg/ffprobe/ffplay)
using Microsoft.Win32; // diálogos nativos de Windows (OpenFileDialog, SaveFileDialog)

namespace Fedo.StageLnk.Server.Gui; // espacio de nombres de la GUI del servidor

/// <summary>
/// Panel de operador WPF: unifica la cabina en una sola ventana. Desde aquí se
/// arranca el servidor, se gestiona la cue list, se difunden las órdenes de show
/// (play/stop/fades/carteles), se ve la telemetría en vivo de cada cliente y el
/// log del sistema. Todos los eventos del servidor llegan desde hilos de red y
/// se reenvían a la interfaz mediante el Dispatcher.
/// </summary>
public partial class MainWindow : Window // clase parcial del panel de operador (mitad XAML, mitad código)
{
    private readonly string _libraryRoot; // raíz de la biblioteca de medios del servidor
    private readonly StageLnkServer _server; // referencia al servidor StageLnk
    private readonly ObservableCollection<CueRow> _cues = new(); // filas de cues visibles en el grid
    private readonly ObservableCollection<ClientRow> _clients = new(); // filas de clientes conectados

    private string _projectName = "Sin título"; // nombre actual del proyecto
    private string? _projectPath; // ruta del archivo de proyecto guardado (null si es nuevo)
    private bool _dirty; // indica si hay cambios sin guardar

    public MainWindow(string libraryRoot, bool demo) // constructor: recibe la biblioteca y el modo demo
    {
        InitializeComponent(); // carga el árbol de elementos definido en el XAML
        _libraryRoot = libraryRoot; // guarda la raíz de la biblioteca

        CueGrid.ItemsSource = _cues; // enlaza el grid de cues a la colección observable
        ClientsGrid.ItemsSource = _clients; // enlaza el grid de clientes a su colección

        KindCombo.ItemsSource = Enum.GetValues<CueKind>(); // rellena el combo con los tipos de cue
        KindCombo.SelectedIndex = 0; // preselecciona el primer tipo de cue

        ColorCombo.ItemsSource = ColorOption.Defaults; // rellena el combo con los colores del cartel
        ColorCombo.SelectedIndex = 0; // preselecciona el primer color

        if (demo) // si se pidió el modo demo...
            DemoLibrary.Ensure(libraryRoot); // ...genera la biblioteca de ejemplo si falta

        _server = new StageLnkServer(libraryRoot); // instancia el servidor con la biblioteca
        WireServerEvents(); // suscribe los eventos del servidor a la interfaz
        _server.Start(); // arranca el servidor TCP/UDP
        _ = _server.ScanLibraryAsync(); // lanza el escaneo inicial de medios (fire-and-forget)

        // Muestra el estado del servidor en la barra de estado
        ServerStatus.Text =
            $"Servidor v1.0 · Biblioteca: {libraryRoot} · TCP {ProtocolDefaults.TcpPort} / UDP {ProtocolDefaults.UdpPort}";
        UpdateBadges(); // actualiza los contadores de clientes y medios

        if (_server.Cues.Cues.Count == 0) // si la cue list está vacía...
            AddDemoCues(); // ...añade las cues de demostración
        BroadcastCueListAndRefresh(); // muestra la cue list en el grid y la difunde a los clientes

        NewCueNumberBox.Text = NextCueNumber().ToString(); // propone el siguiente número de cue
        ProjectNameLabel.Text = _projectName; // muestra el nombre del proyecto
        UpdateProjectHeader(); // refresca cabecera, indicador de cambios y título
        Ui("Panel de operador iniciado."); // registra el arranque en el log
    }

    /// <summary>Siguiente número de cue disponible (máximo actual + 1).</summary>
    private int NextCueNumber() // calcula el siguiente número de cue disponible
    {
        var cues = _server.Cues.Cues; // accede a la cue list del servidor
        // Máximo actual + 1 (o 1 si la lista está vacía)
        return (cues.Count == 0 ? 0 : cues.Max(c => c.Number)) + 1;
    }

    private void WireServerEvents() // conecta los eventos del servidor con la interfaz
    {
        _server.Log += m => Ui(m); // cada mensaje de log del servidor se muestra en el panel
        // Un cliente nuevo se añade a la lista en el hilo de la UI
        _server.ClientConnected += s => Dispatcher.BeginInvoke(() =>
        {
            _clients.Add(new ClientRow(s.Id, s.Name)); // añade la fila del cliente conectado
            Ui($"+ Cliente online: {s.Name}"); // informa de la conexión en el log
            UpdateBadges(); // refresca el contador de clientes
        });
        // Un cliente desconectado se elimina de la lista en el hilo de la UI
        _server.ClientDisconnected += id => Dispatcher.BeginInvoke(() =>
        {
            var row = _clients.FirstOrDefault(c => c.Id == id); // busca la fila por identificador
            if (row is not null) // si el cliente estaba registrado...
            {
                _clients.Remove(row); // ...lo quita de la lista
                Ui($"- Cliente offline: {id}"); // informa de la desconexión en el log
            }
            UpdateBadges(); // refresca el contador de clientes
        });
        // Las telemetrías se aplican a su fila en el hilo de la UI
        _server.ClientStatusUpdated += (s, r) => Dispatcher.BeginInvoke(() =>
        {
            var row = _clients.FirstOrDefault(c => c.Id == s.Id); // localiza la fila del cliente
            if (row is null) // si aún no está registrada...
            {
                row = new ClientRow(s.Id, s.Name); // ...crea la fila
                _clients.Add(row); // y la añade a la lista
            }
            row.Update(r); // aplica la telemetría recibida a la fila
        });
        _server.ClientReady += s => Ui($"{s.Name} está READY para show."); // avisa en el log cuando un cliente queda READY
    }

    private void UpdateBadges() // refresca los contadores de clientes y medios
    {
        ClientsBadge.Text = $"● {_clients.Count} cliente(s) conectado(s)"; // muestra el nº de clientes
        LibraryBadge.Text = $"{_server.Library.All.Count} medios en biblioteca"; // muestra el nº de medios
    }

    // ----------------------------------------------------------------- proyecto

    private void UpdateProjectHeader() // actualiza la cabecera del proyecto
    {
        ProjectNameLabel.Text = _projectName; // muestra el nombre del proyecto
        // Muestra u oculta el asterisco según haya cambios sin guardar
        ProjectDirtyStar.Visibility = _dirty ? Visibility.Visible : Visibility.Collapsed;
        ProjectPathLabel.Text = _projectPath ?? ""; // muestra la ruta del archivo (vacío si no hay)
        // Compone el título de la ventana con el nombre y el indicador de cambios
        Title = _projectName + (_dirty ? " *" : "") + " — Fedo StageLnk Panel";
    }

    private void MarkDirty() // marca el proyecto como modificado sin guardar
    {
        if (_dirty) // si ya está marcado...
            return; // ...no hace falta repetir
        _dirty = true; // activa la marca de cambios pendientes
        UpdateProjectHeader(); // refresca la cabecera para mostrar el asterisco
    }

    /// <summary>
    /// Si el proyecto actual tiene cambios sin guardar, pregunta al operador qué
    /// hacer (Guardar / No guardar / Cancelar). Devuelve false si se cancela.
    /// </summary>
    private bool ConfirmSaveProject() // pregunta si guardar antes de descartar cambios
    {
        if (!_dirty) // sin cambios pendientes...
            return true; // ...no hay nada que confirmar

        // Muestra un diálogo de Guardar / No guardar / Cancelar
        var result = MessageBox.Show(this,
            "El proyecto actual tiene cambios sin guardar. ¿Quieres guardarlos antes de continuar?",
            "Proyecto sin guardar", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) // si el operador cancela...
            return false; // ...se aborta la operación en curso
        if (result == MessageBoxResult.No) // si no quiere guardar...
            return true; // ...se continúa descartando los cambios
        return SaveCurrentProject(); // si elige guardar, se guarda y se continúa
    }

    private bool SaveCurrentProject() // guarda el proyecto en su ruta actual
    {
        if (_projectPath is null) // si es un proyecto nuevo...
            return SaveProjectAs(); // ...pide dónde guardarlo
        return WriteProjectFile(_projectPath); // en caso contrario, escribe en la ruta conocida
    }

    private bool SaveProjectAs() // abre el diálogo "guardar como"
    {
        var dlg = new SaveFileDialog // configura el diálogo de guardado
        {
            Title = "Guardar proyecto de show", // título de la ventana del diálogo
            Filter = "Proyecto StageLnk (*.stg)|*.stg|Todos los archivos (*.*)|*.*", // filtros de extensión
            FileName = _projectName + ".stg", // nombre de archivo sugerido
            DefaultExt = ".stg", // extensión por defecto
            AddExtension = true // añade la extensión si el usuario no la escribe
        };
        if (dlg.ShowDialog(this) != true) // si el operador cancela...
            return false; // ...no se guarda nada
        return WriteProjectFile(dlg.FileName); // escribe el archivo en la ruta elegida
    }

    private bool WriteProjectFile(string path) // escribe el proyecto en disco
    {
        try // intenta guardar el proyecto
        {
            _server.SaveProjectAsync(path).Wait(); // guarda de forma síncrona (espera a la tarea)
            _projectPath = path; // recuerda la ruta guardada
            _projectName = Path.GetFileNameWithoutExtension(path); // deriva el nombre del archivo
            _dirty = false; // ya no hay cambios pendientes
            UpdateProjectHeader(); // refresca la cabecera
            Ui($"Proyecto guardado: {path}"); // informa en el log
            return true; // indica éxito
        }
        catch (Exception ex) // si falla el guardado...
        {
            Ui($"Error al guardar el proyecto: {ex.Message}"); // ...muestra el error en el log
            return false; // indica el fallo
        }
    }

    private void ProjectSave_Click(object sender, RoutedEventArgs e) // evento del botón Guardar
    {
        SaveCurrentProject(); // guarda el proyecto actual
    }

    private void ProjectSaveAs_Click(object sender, RoutedEventArgs e) // evento del botón Guardar como
    {
        SaveProjectAs(); // abre el diálogo de guardado
    }

    private void ProjectNew_Click(object sender, RoutedEventArgs e) // evento del botón Nuevo proyecto
    {
        if (!ConfirmSaveProject()) // si el operador cancela el guardado...
            return; // ...se aborta
        _server.Cues.Clear(); // vacía la cue list del servidor
        _projectName = "Sin título"; // restablece el nombre por defecto
        _projectPath = null; // olvida la ruta del proyecto
        _dirty = false; // sin cambios pendientes
        BroadcastCueListAndRefresh(); // refresca el grid y difunde la lista vacía
        NewCueNumberBox.Text = NextCueNumber().ToString(); // reinicia el número sugerido
        UpdateProjectHeader(); // actualiza la cabecera
        Ui("Proyecto nuevo (cue list vacía)."); // informa en el log
    }

    private void ProjectOpen_Click(object sender, RoutedEventArgs e) // evento del botón Abrir
    {
        if (!ConfirmSaveProject()) // si el operador cancela el guardado...
            return; // ...se aborta
        var dlg = new OpenFileDialog // configura el diálogo de apertura
        {
            Title = "Abrir proyecto de show", // título de la ventana del diálogo
            Filter = "Proyecto StageLnk (*.stg)|*.stg|Proyecto clásico (*.fsl)|*.fsl|Todos los archivos (*.*)|*.*", // filtros de extensión
            DefaultExt = ".stg" // extensión por defecto
        };
        if (dlg.ShowDialog(this) != true) // si el operador cancela...
            return; // ...no se abre nada
        try // intenta cargar el proyecto
        {
            _server.LoadProjectAsync(dlg.FileName).Wait(); // carga de forma síncrona (espera a la tarea)
            _projectPath = dlg.FileName; // recuerda la ruta cargada
            _projectName = Path.GetFileNameWithoutExtension(dlg.FileName); // deriva el nombre del archivo
            _dirty = false; // sin cambios pendientes tras la carga
            BroadcastCueListAndRefresh(); // muestra las cues y las difunde a los clientes
            NewCueNumberBox.Text = NextCueNumber().ToString(); // actualiza el número sugerido
            UpdateProjectHeader(); // refresca la cabecera
            Ui($"Proyecto cargado: {dlg.FileName}"); // informa en el log
        }
        catch (Exception ex) // si falla la carga...
        {
            Ui($"Error al cargar el proyecto: {ex.Message}"); // ...muestra el error en el log
        }
    }

    // ------------------------------------------------------------------- medios

    /// <summary>
    /// Importa archivos de medios a la biblioteca del servidor y vuelve a escanear.
    /// El nuevo manifiesto se difunde a los clientes, que descargan automáticamente
    /// los archivos que les faltan (con verificación SHA-256) y quedan READY.
    /// </summary>
    private async void ImportMedia_Click(object sender, RoutedEventArgs e) // handler async: importa medios a la biblioteca
    {
        var dlg = new OpenFileDialog // configura el diálogo de selección de medios
        {
            Title = "Cargar archivos de medios a la biblioteca", // título de la ventana del diálogo
            // Filtro con las extensiones de medios soportadas
            Filter = "Medios (*.mp4;*.mkv;*.mov;*.wav;*.mp3;*.ogg;*.flac;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.mp4;*.mkv;*.mov;*.wav;*.mp3;*.ogg;*.flac;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Todos los archivos (*.*)|*.*",
            Multiselect = true // permite elegir varios archivos
        };
        if (dlg.ShowDialog(this) != true) // si el operador cancela...
            return; // ...no se importa nada

        var imported = CopyFilesToLibrary(dlg.FileNames); // copia los archivos elegidos a la biblioteca
        if (imported.Count == 0) // si no se copió nada...
            return; // ...se sale sin más

        Ui($"Importando {imported.Count} archivo(s): {string.Join(", ", imported)}…"); // informa de los archivos copiados
        try // intenta reescanear la biblioteca
        {
            await _server.ScanLibraryAsync(); // espera el nuevo escaneo de la biblioteca
            UpdateBadges(); // refresca el contador de medios
            MarkDirty(); // el proyecto queda marcado como modificado
            // Informa del total de medios y de la descarga automática en los clientes
            Ui($"Biblioteca actualizada: {_server.Library.All.Count} medios. Los clientes descargarán los archivos que les falten.");
        }
        catch (Exception ex) // si falla el escaneo...
        {
            Ui($"Error al escanear la biblioteca: {ex.Message}"); // ...muestra el error en el log
        }
    }

    /// <summary>
    /// Copia archivos a la biblioteca del servidor (con renombrado " (2)" si el
    /// nombre ya existe) y devuelve las rutas relativas resultantes. Si el archivo
    /// ya estaba en la biblioteca, se devuelve sin copiar.
    /// </summary>
    private List<string> CopyFilesToLibrary(IEnumerable<string> files) // copia archivos a la biblioteca y devuelve nombres
    {
        Directory.CreateDirectory(_libraryRoot); // asegura que exista la carpeta de biblioteca
        var copied = new List<string>(); // lista de nombres de archivos copiados
        foreach (string file in files) // recorre cada archivo seleccionado
        {
            string name = Path.GetFileName(file); // extrae el nombre del archivo
            string target = Path.Combine(_libraryRoot, name); // ruta de destino en la biblioteca
            // Si el archivo ya está en la biblioteca, se reutiliza sin copiar
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                copied.Add(name); // se registra el nombre ya existente
                continue; // salta a la siguiente iteración
            }
            if (File.Exists(target)) // si el destino ya ocupa el nombre...
            {
                string baseName = Path.GetFileNameWithoutExtension(name); // nombre sin extensión
                string ext = Path.GetExtension(name); // extensión del archivo
                for (int i = 2; ; i++) // busca un sufijo "(2)", "(3)"... libre
                {
                    string alt = $"{baseName} ({i}){ext}"; // candidato con sufijo incremental
                    if (!File.Exists(Path.Combine(_libraryRoot, alt))) // si el candidato está libre...
                    {
                        target = Path.Combine(_libraryRoot, alt); // ...se usa como destino
                        break; // deja de buscar
                    }
                }
            }
            File.Copy(file, target); // copia el archivo a la biblioteca
            copied.Add(Path.GetFileName(target)); // registra el nombre final del archivo copiado
        }
        return copied; // devuelve los nombres copiados
    }

    // Mapea un tipo de medio a su tipo de cue equivalente (null si no hay equivalente)
    private static CueKind? CueKindFor(MediaKind kind) => kind switch
    {
        MediaKind.Video => CueKind.Video, // vídeo → cue de vídeo
        MediaKind.Image => CueKind.Image, // imagen → cue de imagen
        MediaKind.Audio => CueKind.Audio, // audio → cue de audio
        _ => null // el resto no tiene cue equivalente
    };

    // ---------------------------------------------------------------- cue list

    private void RefreshCues() // reconstruye las filas del grid a partir de la cue list
    {
        _cues.Clear(); // vacía la colección visible
        foreach (var cue in _server.Cues.Cues) // recorre las cues del servidor...
            _cues.Add(new CueRow(cue)); // ...y las añade como filas visibles
    }

    private void AddDemoCues() // crea un conjunto de cues de demostración
    {
        AddCue(1, "Tono de prueba", CueKind.Audio); // cue 1: tono de prueba (audio)
        AddCue(2, "Intro video", CueKind.Video); // cue 2: vídeo de introducción
        AddCue(3, "Logo", CueKind.Image); // cue 3: logotipo (imagen)
        AddCue(4, "FEDO-STAGELNK", CueKind.Text); // cue 4: texto de marca

        var fx = new Cue { Number = 5, Name = "FX FFT", Kind = CueKind.VisualFx }; // cue 5: efecto visual audio-reactivo
        string fxFile = Path.Combine(_libraryRoot, "FX Beat.wav"); // ruta de la pista para el efecto
        if (File.Exists(fxFile)) // si la pista existe en la biblioteca...
            fx.Layers.Add(new LayerSpec { ZOrder = 0, MediaId = Hash.Sha256File(fxFile) }); // ...se enlaza como capa de audio
        _server.UpsertCue(fx); // registra la cue FX en el servidor
        Ui("Cues de demostración creadas (1-5, incluye FX FFT audio-reactivo)."); // informa en el log
    }

    private void AddCue(int number, string name, CueKind kind) // añade una cue al servidor con auto-vinculación de medio
    {
        var cue = new Cue { Number = number, Name = name, Kind = kind }; // construye la cue
        // Determina el tipo de medio que se busca para auto-vincular
        MediaKind? wantKind = kind switch
        {
            CueKind.Audio => MediaKind.Audio, // audio busca un medio de audio
            CueKind.Video => MediaKind.Video, // vídeo busca un medio de vídeo
            CueKind.Image => MediaKind.Image, // imagen busca un medio de imagen
            CueKind.VisualFx => MediaKind.Audio, // el FX usa audio como fuente de energía
            _ => null // el resto no auto-vincula medio
        };
        if (wantKind is not null) // si hay un tipo de medio buscado...
        {
            // Busca el medio mejor encajado por tipo y coincidencia de nombre
            var media = _server.Library.All
                .Where(a => a.Kind == wantKind.Value)
                .OrderByDescending(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .ThenBy(a => a.Name)
                .FirstOrDefault();
            if (media is not null) // si se encontró un medio compatible...
                cue.Layers.Add(new LayerSpec { ZOrder = 0, MediaId = media.Id }); // ...se enlaza como capa
            else // si no hay medio disponible...
                // Avisa de que la cue se creó sin capa de medio
                Ui($"Aviso: no hay medio {wantKind.Value} en la biblioteca; la cue {number} se creó sin capa.");
        }
        _server.UpsertCue(cue); // guarda la cue en el servidor
    }

    private void BroadcastCueListAndRefresh() // refresca el grid y difunde la lista a los clientes
    {
        RefreshCues(); // actualiza la vista de la cue list
        _ = _server.BroadcastCueListAsync(); // difunde el manifiesto sin esperar (fire-and-forget)
    }

    // --------------------------------------------------------------- transporte

    private int? RequestedCue() // determina qué cue debe reproducirse
    {
        if (int.TryParse(CueNumberBox.Text.Trim(), out int n) && n > 0) // si el cuadro trae un número válido...
            return n; // ...se usa ese número
        return (CueGrid.SelectedItem as CueRow)?.Number; // si no, usa la cue seleccionada en el grid
    }

    private async void Play_Click(object sender, RoutedEventArgs e) // handler async: botón Play
    {
        int? n = RequestedCue(); // resuelve el número de cue a reproducir
        if (n is null) // si no hay cue indicada...
        {
            Ui("Indica un número de cue o selecciona una de la lista."); // ...avisa al operador
            return; // y sale
        }
        await _server.PlayCueAsync(n.Value); // difunde la orden de reproducción a los clientes
    }

    private void CueGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) // doble clic en una cue del grid
    {
        if (CueGrid.SelectedItem is CueRow row) // si hay una fila seleccionada...
            _ = _server.PlayCueAsync(row.Number); // ...la reproduce sin esperar (fire-and-forget)
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await _server.StopAsync(); // handler async: detiene la reproducción
    private async void Pause_Click(object sender, RoutedEventArgs e) => await _server.PauseAsync(); // handler async: pausa la cue en curso
    private async void Resume_Click(object sender, RoutedEventArgs e) => await _server.ResumeAsync(); // handler async: reanuda la cue pausada
    private async void Next_Click(object sender, RoutedEventArgs e) => await _server.NextAsync(); // handler async: salta a la siguiente cue
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _server.PreviousAsync(); // handler async: vuelve a la cue anterior
    private async void Freeze_Click(object sender, RoutedEventArgs e) => await _server.FreezeAsync(); // handler async: congela el frame actual
    private async void Black_Click(object sender, RoutedEventArgs e) => await _server.BlackOutAsync(); // handler async: corte a negro
    private async void Fade_Click(object sender, RoutedEventArgs e) => await _server.FadeAsync(3.0, toBlack: true); // handler async: fundido a negro en 3 s

    // ------------------------------------------------------------------- cartel

    private async void ShowSign_Click(object sender, RoutedEventArgs e) // handler async: mostrar cartel
    {
        string text = SignTextBox.Text.Trim(); // lee y recorta el texto del cartel
        if (text.Length == 0) // si no hay texto...
        {
            Ui("Escribe el texto del anuncio."); // ...avisa al operador
            return; // y sale
        }
        string color = (ColorCombo.SelectedItem as ColorOption)?.Hex ?? "#FFD700"; // color elegido (dorado por defecto)
        await _server.BroadcastTextAsync(text, 96, color); // difunde el cartel con tamaño 96 a los clientes
    }

    // -------------------------------------------------------------- añadir/borrar

    private async void AddCue_Click(object sender, RoutedEventArgs e) // handler async: añadir cue
    {
        // Toma el número del cuadro si es válido; si no, el siguiente disponible
        int number = int.TryParse(NewCueNumberBox.Text.Trim(), out int n) && n > 0
            ? n
            : NextCueNumber();

        var dlg = new OpenFileDialog // configura el diálogo para elegir el medio
        {
            Title = "Elegir el medio de la cue (se copia a la biblioteca del servidor)", // título del diálogo
            // Filtro con las extensiones de medios soportadas
            Filter = "Medios (*.mp4;*.mkv;*.mov;*.wav;*.mp3;*.ogg;*.flac;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.mp4;*.mkv;*.mov;*.wav;*.mp3;*.ogg;*.flac;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Todos los archivos (*.*)|*.*",
            Multiselect = false // solo se elige un archivo
        };

        if (dlg.ShowDialog(this) == true) // si el operador eligió un archivo...
        {
            await AddCueWithMediaAsync(number, dlg.FileName); // ...añade la cue con ese medio
            return; // termina aquí
        }

        AddCueFromForm(number); // si no eligió medio, crea la cue desde el formulario
    }

    /// <summary>
    /// Añade una cue a partir de un archivo elegido: lo copia a la biblioteca del
    /// servidor, lo vincula como capa de la cue y difunde el nuevo manifiesto para
    /// que los clientes descarguen el medio automáticamente.
    /// </summary>
    private async Task AddCueWithMediaAsync(int number, string file) // añade una cue vinculando un archivo
    {
        MediaKind? mediaKind = MediaAsset.KindForExtension(Path.GetExtension(file)); // deduce el tipo de medio por extensión
        if (mediaKind is null) // si la extensión no es soportada...
        {
            Ui($"Tipo de archivo no soportado: {Path.GetExtension(file)}"); // ...avisa al operador
            return; // y sale
        }
        CueKind? cueKind = CueKindFor(mediaKind.Value); // deduce el tipo de cue equivalente
        if (cueKind is null) // si no hay cue equivalente...
        {
            Ui($"El medio {mediaKind.Value} no tiene cue equivalente en el sistema."); // ...avisa al operador
            return; // y sale
        }

        string cueName = NewCueNameBox.Text.Trim(); // nombre de la cue desde el formulario
        if (cueName.Length == 0) // si está vacío...
            cueName = Path.GetFileNameWithoutExtension(file); // ...usa el nombre del archivo

        var copied = CopyFilesToLibrary(new[] { file }); // copia el archivo a la biblioteca
        if (copied.Count == 0) // si no se copió...
            return; // ...no se puede continuar
        string rel = copied[0]; // ruta relativa del archivo copiado

        Ui($"Importando '{rel}' a la biblioteca…"); // informa del inicio de la importación
        try // intenta reescanear la biblioteca
        {
            await _server.ScanLibraryAsync(); // espera el escaneo para indexar el nuevo archivo
        }
        catch (Exception ex) // si falla el escaneo...
        {
            Ui($"Error al escanear la biblioteca: {ex.Message}"); // ...muestra el error en el log
            return; // y abandona
        }
        UpdateBadges(); // refresca el contador de medios

        // Busca el activo recién importado por su ruta relativa
        var asset = _server.Library.All.FirstOrDefault(a =>
            string.Equals(a.RelativePath, rel, StringComparison.OrdinalIgnoreCase));
        if (asset is null) // si el activo no aparece indexado...
        {
            Ui($"El archivo se copió pero no se encontró en la biblioteca: {rel}"); // ...avisa al operador
            return; // y sale
        }

        var cue = new Cue { Number = number, Name = cueName, Kind = cueKind.Value }; // construye la cue
        cue.Layers.Add(new LayerSpec { ZOrder = 0, MediaId = asset.Id }); // enlaza el medio como capa base
        _server.UpsertCue(cue); // registra la cue en el servidor

        BroadcastCueListAndRefresh(); // refresca el grid y difunde la lista a los clientes
        MarkDirty(); // el proyecto queda marcado como modificado
        // Informa de la cue creada y de la descarga automática del medio
        Ui($"Cue {number:000} '{cueName}' [{cueKind}] con medio '{asset.Name}'. Los clientes lo descargarán y quedarán READY.");

        NewCueNumberBox.Text = NextCueNumber().ToString(); // propone el siguiente número
        NewCueNameBox.Text = ""; // limpia el campo de nombre
        NewCueNameBox.Focus(); // devuelve el foco al campo de nombre
    }

    /// <summary>
    /// Añade una cue sin medio elegido por el usuario: auto-vincula por nombre/tipo
    /// si existe un medio compatible en la biblioteca (comportamiento anterior).
    /// </summary>
    private void AddCueFromForm(int number) // añade una cue sin medio elegido por el usuario
    {
        string name = NewCueNameBox.Text.Trim(); // nombre desde el formulario
        if (name.Length == 0) // si está vacío...
            name = $"Cue {number}"; // ...usa un nombre genérico
        // Usa el tipo elegido en el combo (por defecto Vídeo)
        var kind = KindCombo.SelectedItem is CueKind k ? k : CueKind.Video;

        AddCue(number, name, kind); // crea la cue (con auto-vinculación de medio si existe)
        BroadcastCueListAndRefresh(); // refresca el grid y difunde la lista
        MarkDirty(); // marca el proyecto como modificado
        Ui($"Cue {number:000} '{name}' [{kind}] añadida."); // informa en el log
        NewCueNumberBox.Text = NextCueNumber().ToString(); // propone el siguiente número
        NewCueNameBox.Text = ""; // limpia el campo de nombre
        NewCueNameBox.Focus(); // devuelve el foco al campo de nombre
    }

    private async void Fx_Click(object sender, RoutedEventArgs e) // handler async: lanzar el FX audio-reactivo
    {
        var fx = _server.Cues.Cues.FirstOrDefault(c => c.Kind == CueKind.VisualFx); // busca la cue VisualFx
        if (fx is null) // si no existe ninguna...
        {
            // Avisa de que debe crearse una cue VisualFx
            Ui("No hay ninguna cue VisualFx. Añade una con Kind 'VisualFx' (llevará la primera capa de audio).");
            return; // y sale
        }
        await _server.PlayCueAsync(fx.Number); // reproduce la cue FX
        Ui($"FX audio-reactivo: cue {fx.Number:000} '{fx.Name}' difundida."); // informa en el log
    }

    private void DeleteCue_Click(object sender, RoutedEventArgs e) // evento del botón de eliminar cue
    {
        if (CueGrid.SelectedItem is not CueRow row) // si no hay cue seleccionada...
        {
            Ui("Selecciona una cue para eliminar."); // ...avisa al operador
            return; // y sale
        }
        _server.Cues.Remove(row.Number); // elimina la cue del servidor
        BroadcastCueListAndRefresh(); // refresca el grid y difunde la lista
        MarkDirty(); // marca el proyecto como modificado
        Ui($"Cue {row.Number:000} eliminada."); // informa en el log
    }

    // ---------------------------------------------------------------------- log

    private void Ui(string message) // muestra un mensaje en el log del panel
    {
        Dispatcher.BeginInvoke(() => // encola el trabajo en el hilo de la UI
        {
            LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}"); // añade la entrada con la hora actual
            if (LogList.Items.Count > 500) // si el log supera 500 entradas...
                LogList.Items.RemoveAt(0); // ...se elimina la más antigua
            LogList.ScrollIntoView(LogList.Items[^1]); // desplaza la vista hasta la última entrada
        });
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogList.Items.Clear(); // evento del botón: vacía el log

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) // se invoca al cerrar la ventana
    {
        if (!ConfirmSaveProject()) // si el operador cancela el guardado...
        {
            e.Cancel = true; // ...se cancela el cierre
            return; // y sale
        }
        _ = _server.DisposeAsync(); // cierra el servidor sin esperar (fire-and-forget)
        base.OnClosing(e); // continúa con el cierre normal de la ventana
    }
}

/// <summary>Fila de la cue list mostrada en el grid.</summary>
public sealed class CueRow // fila inmutable de la cue list para el grid
{
    public CueRow(Cue cue) // constructor a partir de una cue del servidor
    {
        Number = cue.Number; // número de la cue
        Name = cue.Name; // nombre de la cue
        Kind = cue.Kind.ToString(); // tipo de cue como texto
        Duration = cue.Duration?.ToString("0") ?? ""; // duración en segundos (vacío si no hay)
        Layers = cue.Layers.Count; // número de capas de la cue
    }

    public int Number { get; } // número de la cue (get-only)
    public string Name { get; } // nombre de la cue (get-only)
    public string Kind { get; } // tipo de cue (get-only)
    public string Duration { get; } // duración formateada (get-only)
    public int Layers { get; } // nº de capas (get-only)
}

/// <summary>Fila de cliente con telemetría en vivo (notifica cambios a la UI).</summary>
public sealed class ClientRow : INotifyPropertyChanged // fila de cliente con notificación de cambios a la UI
{
    public Guid Id { get; } // identificador único del cliente
    public string Name { get; } // nombre del cliente

    private string _status = "conectando"; // estado inicial de conexión
    private string _fps = "0"; // fotogramas por segundo iniciales
    private string _cpu = "-"; // uso de CPU (desconocido)
    private string _gpuUsage = "-"; // uso de GPU (desconocido)
    private string _gpu = "-"; // modelo de GPU (desconocido)
    private string _ram = "-"; // memoria usada (desconocido)
    private string _temp = "-"; // temperatura (desconocido)
    private string _resolution = "-"; // resolución de salida (desconocido)
    private string _cue = "0"; // última cue reproducida
    private string _sync = ""; // estado de sincronización

    public ClientRow(Guid id, string name) // constructor con identificador y nombre
    {
        Id = id; // fija el identificador
        Name = name; // fija el nombre
    }

    public string Status { get => _status; private set => Set(ref _status, value); } // estado de conexión (notifica cambios)
    public string Fps { get => _fps; private set => Set(ref _fps, value); } // fps del cliente
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); } // uso de CPU
    public string GpuUsage { get => _gpuUsage; private set => Set(ref _gpuUsage, value); } // uso de GPU
    public string Gpu { get => _gpu; private set => Set(ref _gpu, value); } // modelo de GPU
    public string Ram { get => _ram; private set => Set(ref _ram, value); } // RAM usada
    public string Temp { get => _temp; private set => Set(ref _temp, value); } // temperatura
    public string Resolution { get => _resolution; private set => Set(ref _resolution, value); } // resolución
    public string Cue { get => _cue; private set => Set(ref _cue, value); } // última cue
    public string Sync { get => _sync; private set => Set(ref _sync, value); } // sincronización

    public void Update(ClientStatusReport r) // aplica un informe de telemetría a la fila
    {
        Status = r.Status.ToString(); // actualiza el estado
        Fps = r.Fps.ToString("0"); // actualiza los fps
        // Muestra el uso de CPU solo si el valor es válido
        Cpu = r.CpuUsage >= 0 ? r.CpuUsage.ToString("0") : "-";
        // Muestra el uso de GPU solo si el valor es válido
        GpuUsage = r.GpuUsage >= 0 ? r.GpuUsage.ToString("0") : "-";
        // Muestra el modelo de GPU acortado si lo hay
        Gpu = string.IsNullOrWhiteSpace(r.Gpu) ? "-" : Shorten(r.Gpu);
        // Muestra la RAM si el valor es positivo
        Ram = r.RamUsedMb > 0 ? $"{r.RamUsedMb} MB" : "-";
        // Muestra la temperatura si el valor es positivo
        Temp = r.TemperatureC > 0 ? $"{r.TemperatureC:0}°C" : "-";
        // Muestra la resolución si es válida
        Resolution = r.ResolutionWidth > 0 ? $"{r.ResolutionWidth}x{r.ResolutionHeight}" : "-";
        Cue = r.LastCueNumber.ToString(); // última cue reproducida
        Sync = r.SyncState ?? ""; // estado de sincronización (vacío si no hay)
    }

    // Acorta el nombre de la GPU a 26 caracteres con elipsis
    private static string Shorten(string gpu)
        => gpu.Length > 26 ? gpu[..26] + "…" : gpu;

    public event PropertyChangedEventHandler? PropertyChanged; // evento de notificación de cambios

    private void Set(ref string field, string value, [CallerMemberName] string? name = null) // helper para asignar y notificar
    {
        if (field == value) // si el valor no cambió...
            return; // ...no se notifica
        field = value; // asigna el nuevo valor
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); // notifica el cambio a la UI
    }
}

/// <summary>Opción de color del cartel 3D.</summary>
public sealed record ColorOption(string Hex, string Name) // opción de color del cartel (hex + nombre)
{
    public static IReadOnlyList<ColorOption> Defaults { get; } = new[] // paleta de colores disponibles
    {
        new ColorOption("#FFD700", "Dorado"), // color dorado
        new ColorOption("#FF3B30", "Rojo"), // color rojo
        new ColorOption("#34C759", "Verde"), // color verde
        new ColorOption("#0A84FF", "Azul"), // color azul
        new ColorOption("#FF9500", "Naranja"), // color naranja
        new ColorOption("#00C7BE", "Cian"), // color cian
        new ColorOption("#FFFFFF", "Blanco") // color blanco
    };
}

/// <summary>
/// Genera una biblioteca de ejemplo (solo si falta): un tono WAV de 3 s en .NET
/// puro y, si ffmpeg está disponible, un vídeo de prueba y un logotipo PNG.
/// </summary>
public static class DemoLibrary // genera una biblioteca de ejemplo si falta
{
    public static void Ensure(string root) // asegura que existan los medios de demostración
    {
        Directory.CreateDirectory(root); // crea la carpeta raíz si no existe

        string tone = Path.Combine(root, "Tono de prueba.wav"); // ruta del tono de prueba
        if (!File.Exists(tone)) // si el tono no existe...
            File.WriteAllBytes(tone, GenerateToneWav(440.0, 3.0)); // ...se genera un WAV de 440 Hz / 3 s

        string beat = Path.Combine(root, "FX Beat.wav"); // ruta de la pista para el FX
        if (!File.Exists(beat)) // si la pista no existe...
            File.WriteAllBytes(beat, GenerateBeatWav()); // ...se genera la pista rítmica

        if (!FfmpegAvailable()) // si ffmpeg no está instalado...
            return; // ...no se pueden generar vídeo ni imagen

        string video = Path.Combine(root, "Intro video.mp4"); // ruta del vídeo de prueba
        if (!File.Exists(video)) // si el vídeo no existe...
            // Genera un vídeo de prueba con ffmpeg (testsrc2, 720p, 5 s)
            Run("ffmpeg", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=5",
                "-pix_fmt", "yuv420p", "-y", video);

        string logo = Path.Combine(root, "Logo.png"); // ruta del logotipo
        if (!File.Exists(logo)) // si el logotipo no existe...
            // Genera un PNG sólido con ffmpeg para usarlo de logotipo
            Run("ffmpeg", "-f", "lavfi", "-i", "color=c=0x1a1a2e:s=800x600:d=1", "-frames:v", "1", "-y", logo);
    }

    private static bool FfmpegAvailable() // comprueba si ffmpeg está disponible en el sistema
    {
        return ExternalTools.IsFfmpegAvailable; // Delega en el resolvedor compartido
    }

    private static void Run(string exe, params string[] args) // ejecuta un comando externo con argumentos
    {
        try // intenta lanzar el comando
        {
            var psi = new ProcessStartInfo(exe) // configura el proceso
            {
                UseShellExecute = false, // sin shell
                CreateNoWindow = true, // sin ventana de consola
                RedirectStandardOutput = true, // captura la salida estándar
                RedirectStandardError = true // captura la salida de error
            };
            foreach (string a in args) // recorre los argumentos...
                psi.ArgumentList.Add(a); // ...y los añade a la lista del proceso
            using var proc = Process.Start(psi); // inicia el proceso
            if (proc is null) // si no se pudo iniciar...
                return; // ...se sale silenciosamente
            proc.StandardOutput.ReadToEnd(); // drena la salida estándar
            proc.StandardError.ReadToEnd(); // drena la salida de error
            proc.WaitForExit(20000); // espera hasta 20 s al cierre
        }
        catch // si la ejecución falla...
        {
        }
    }

    private static byte[] GenerateToneWav(double frequency, double seconds) // genera un WAV PCM de un tono puro
    {
        const int sampleRate = 44100; // frecuencia de muestreo
        int sampleCount = (int)(sampleRate * seconds); // número total de muestras
        int dataSize = sampleCount * 2; // tamaño de los datos de audio (16 bits por muestra)
        using var ms = new MemoryStream(); // buffer de memoria para el WAV
        using var w = new BinaryWriter(ms); // escritor binario sobre el buffer
        w.Write("RIFF"u8); // cabecera RIFF
        w.Write(36 + dataSize); // tamaño total del archivo
        w.Write("WAVE"u8); // formato WAVE
        w.Write("fmt "u8); // chunk de formato
        w.Write(16); // tamaño del chunk de formato
        w.Write((short)1); // PCM sin compresión
        w.Write((short)1); // mono
        w.Write(sampleRate); // frecuencia de muestreo
        w.Write(sampleRate * 2); // bit rate (muestras × bytes por muestra)
        w.Write((short)2); // bloque de 2 bytes
        w.Write((short)16); // 16 bits por muestra
        w.Write("data"u8); // chunk de datos
        w.Write(dataSize); // tamaño de los datos

        double amplitude = short.MaxValue * 0.5; // amplitud máxima (50% del rango)
        for (int i = 0; i < sampleCount; i++) // recorre cada muestra
        {
            // Envolvente de ataque/decaimiento para evitar clics al inicio y al final
            double envelope = Math.Min(1.0, Math.Min(i / (sampleRate * 0.05), (sampleCount - i) / (sampleRate * 0.05)));
            double sample = amplitude * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate); // muestra de la onda senoidal
            w.Write((short)sample); // escribe la muestra como entero de 16 bits
        }
        return ms.ToArray(); // devuelve el WAV completo
    }

    /// <summary>
    /// Pista rítmica de 8 s para el FX audio-reactivo: patrón 120 BPM con golpes de
    /// bombo (55 Hz), nota de graves (110 Hz) y sombrero (ruido 6 kHz). La energía
    /// de graves varía de forma marcada para que el FFT detecte beats y mueva las
    /// barras del visualizador.
    /// </summary>
    private static byte[] GenerateBeatWav() // genera la pista rítmica para el FX audio-reactivo
    {
        const int sampleRate = 44100; // frecuencia de muestreo
        const double bpm = 120.0; // tempo de la pista
        const double seconds = 8.0; // duración total
        int sampleCount = (int)(sampleRate * seconds); // número total de muestras
        var buffer = new float[sampleCount]; // buffer flotante de muestras
        var random = new Random(2026); // generador con semilla fija para reproducibilidad
        double beatSeconds = 60.0 / bpm; // duración de un beat

        void Kick(double start, double dur, double amp) // genera un golpe de bombo
        {
            int s = (int)(start * sampleRate); // muestra inicial
            int n = (int)(dur * sampleRate); // duración en muestras
            for (int i = 0; i < n && s + i < sampleCount; i++) // recorre la duración del golpe
            {
                double t = i / (double)sampleRate; // tiempo relativo en segundos
                double env = Math.Exp(-t * 28.0); // envolvente de decaimiento rápido
                double tone = Math.Sin(2 * Math.PI * (55.0 - 30.0 * (1 - Math.Exp(-t * 40))) * t); // tono con barrido descendente
                buffer[s + i] += (float)(amp * env * tone); // suma el golpe al buffer
            }
        }

        void Hat(double start, double dur, double amp) // genera un sombrero (ruido)
        {
            int s = (int)(start * sampleRate); // muestra inicial
            int n = (int)(dur * sampleRate); // duración en muestras
            for (int i = 0; i < n && s + i < sampleCount; i++) // recorre la duración del sombrero
            {
                double env = Math.Exp(-i / (double)sampleRate * 220.0); // envolvente de decaimiento
                buffer[s + i] += (float)(amp * env * (random.NextDouble() * 2 - 1)); // suma ruido blanco amortiguado
            }
        }

        void Bass(double start, double dur, double amp, double freq) // genera una nota de graves
        {
            int s = (int)(start * sampleRate); // muestra inicial
            int n = (int)(dur * sampleRate); // duración en muestras
            for (int i = 0; i < n && s + i < sampleCount; i++) // recorre la duración de la nota
            {
                double t = i / (double)sampleRate; // tiempo relativo en segundos
                double env = Math.Exp(-t * 2.2); // envolvente de decaimiento suave
                buffer[s + i] += (float)(amp * env * Math.Sin(2 * Math.PI * freq * t)); // suma la onda de graves
            }
        }

        for (int b = 0; b < (int)(seconds / beatSeconds); b++) // recorre los beats del patrón
        {
            double start = b * beatSeconds; // instante del beat
            Kick(start, 0.16, 0.9); // bombo en el beat
            Hat(start, 0.05, 0.22); // sombrero en el beat
            Hat(start + beatSeconds / 2, 0.05, 0.18); // sombrero en el contratiempo
            Bass(start, 0.28, 0.35, 110.0); // graves (A2) en el beat
            Bass(start + beatSeconds / 2, 0.18, 0.28, 146.83); // graves (D3) en el contratiempo
        }

        using var ms = new MemoryStream(); // buffer de memoria para el WAV
        using var w = new BinaryWriter(ms); // escritor binario sobre el buffer
        int dataSize = sampleCount * 2; // tamaño de los datos de audio
        w.Write("RIFF"u8); // cabecera RIFF
        w.Write(36 + dataSize); // tamaño total del archivo
        w.Write("WAVE"u8); // formato WAVE
        w.Write("fmt "u8); // chunk de formato
        w.Write(16); // tamaño del chunk de formato
        w.Write((short)1); // PCM sin compresión
        w.Write((short)1); // mono
        w.Write(sampleRate); // frecuencia de muestreo
        w.Write(sampleRate * 2); // bit rate
        w.Write((short)2); // bloque de 2 bytes
        w.Write((short)16); // 16 bits por muestra
        w.Write("data"u8); // chunk de datos
        w.Write(dataSize); // tamaño de los datos

        for (int i = 0; i < sampleCount; i++) // recorre cada muestra
        {
            // Envolvente general para evitar clics al inicio y al final
            double env = Math.Min(1.0, Math.Min(i / (double)(sampleRate / 2), (sampleCount - i) / (double)(sampleRate / 2)));
            w.Write((short)(short.MaxValue * 0.7 * Math.Clamp(buffer[i], -1, 1) * env)); // normaliza y escribe la muestra
        }
        return ms.ToArray(); // devuelve el WAV completo
    }
}
