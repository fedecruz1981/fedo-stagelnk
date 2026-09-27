// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server.Gui :: App
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.IO; // operaciones de archivos y rutas (Path, Environment)
using System.Windows; // tipos base de WPF (Application, StartupEventArgs)

namespace Fedo.StageLnk.Server.Gui; // espacio de nombres de la GUI del servidor

/// <summary>
/// Punto de entrada del panel de operador. Analiza los argumentos de línea de
/// comandos: la ruta de la biblioteca (primer argumento sin guion) y la opción
/// --demo que genera una biblioteca de ejemplo con tono, vídeo e imagen.
/// </summary>
public partial class App : Application // clase parcial de la aplicación (mitad XAML, mitad código)
{
    protected override void OnStartup(StartupEventArgs e) // se invoca al arrancar la aplicación
    {
        base.OnStartup(e); // ejecuta el arranque base de WPF

        // Resuelve el primer argumento sin guion como ruta de la biblioteca (fallback en Library\Server)
        string libraryRoot = e.Args.FirstOrDefault(a => !a.StartsWith("-"))
            ?? Path.Combine(Environment.CurrentDirectory, "Library", "Server");
        bool demo = e.Args.Contains("--demo"); // modo demo: genera una biblioteca de ejemplo

        var window = new MainWindow(libraryRoot, demo); // crea la ventana principal con la ruta y el modo demo
        window.Show(); // muestra el panel de operador
    }
}
