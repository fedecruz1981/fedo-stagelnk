// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: MonitorHelper
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

// Importa los atributos de interoperabilidad con Win32 (DllImport, StructLayout).
using System.Runtime.InteropServices;

namespace Fedo.StageLnk.Client;

/// <summary>
/// Área útil (bounds) de un monitor, en coordenadas de escritorio (el monitor
/// principal empieza en 0,0; los extendidos pueden tener coordenadas negativas).
/// </summary>
// Registro inmutable con la posición y el tamaño de un monitor.
public readonly record struct DisplayBounds(int X, int Y, int Width, int Height);

/// <summary>
/// Enumera los monitores del sistema mediante Win32 (EnumDisplayMonitors), sin
/// depender de WPF ni WinForms. Se usa para abrir el render en la pantalla que
/// elija el operador (típicamente el proyector / pantalla extendida).
/// </summary>
// Clase estática que consulta los monitores del sistema vía la API Win32.
public static class MonitorHelper
{
    // Devuelve la lista de áreas útiles de todos los monitores detectados.
    public static IReadOnlyList<DisplayBounds> Enumerate()
    {
        // Lista donde se acumulan los bounds de cada monitor.
        var list = new List<DisplayBounds>();
        // Llama a la API Win32 que invoca el delegado por cada monitor.
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            // Delegado de retrollamada: se ejecuta una vez por cada monitor.
            (hMonitor, hdc, lprc, lParam) =>
            {
                // Convierte el puntero RECT de Win32 a la estructura nativa.
                var rect = Marshal.PtrToStructure<NativeRect>(lprc);
                // Añade el monitor con su posición y tamaño calculados.
                list.Add(new DisplayBounds(
                    rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top));
                // Devuelve true para continuar enumerando el siguiente monitor.
                return true;
            }, IntPtr.Zero);
        return list;
    }

    // Estructura nativa RECT de Win32 con las coordenadas del rectángulo.
    [StructLayout(LayoutKind.Sequential)]
    // Declara el layout secuencial para que coincida con el RECT nativo.
    private struct NativeRect
    {
        // Coordenada izquierda, superior, derecha e inferior del rectángulo.
        public int Left, Top, Right, Bottom;
    }

    // Firma del callback de EnumDisplayMonitors (delegado nativo).
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr lParam);

    // P/Invoke de user32.dll: enumera los monitores llamando a lpfnEnum por cada uno.
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
}
