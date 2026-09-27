// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: SystemMetrics
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics; // Proporciona Process y ProcessStartInfo para lanzar nvidia-smi
using System.Runtime.InteropServices; // Soporte de interoperabilidad con código nativo
using System.Management; // Acceso a WMI para datos de GPU y temperatura

namespace Fedo.StageLnk.Client; // Espacio de nombres del cliente Fedo-StageLnk

/// <summary>Telemetría ligera del sistema para el reporte de estado en vivo.</summary>
public sealed class SystemMetrics
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5); // Caducidad de la caché de métricas (5 segundos)

    private readonly Process _process = Process.GetCurrentProcess(); // Proceso actual para medir su consumo de CPU
    private DateTimeOffset _cpuSampleUtc = DateTimeOffset.UtcNow; // Última muestra de tiempo para el cálculo de CPU
    private TimeSpan _cpuTotal = Process.GetCurrentProcess().TotalProcessorTime; // Tiempo de CPU acumulado del proceso

    private string _gpuName = ""; // Nombre de la GPU detectada
    private int _resolutionWidth; // Resolución horizontal actual de pantalla
    private int _resolutionHeight; // Resolución vertical actual de pantalla
    private DateTimeOffset _gpuCacheUntil = DateTimeOffset.MinValue; // Fin de validez de la caché de GPU

    private double _temperatureC = -1; // Temperatura en grados Celsius (en caché); -1 = sin dato
    private DateTimeOffset _tempCacheUntil = DateTimeOffset.MinValue; // Fin de validez de la caché de temperatura

    private double _gpuUsage = -1; // Uso de GPU en porcentaje; -1 = sin dato
    private DateTimeOffset _gpuUsageCacheUntil = DateTimeOffset.MinValue; // Fin de validez de la caché de uso de GPU

    public double GetCpuUsagePercent()
    {
        try
        {
            var now = DateTimeOffset.UtcNow; // Hora actual para tomar la muestra
            var total = _process.TotalProcessorTime; // Tiempo de CPU total del proceso en este instante
            double elapsedSec = (now - _cpuSampleUtc).TotalSeconds; // Segundos transcurridos desde la última muestra
            double deltaSec = (total - _cpuTotal).TotalSeconds; // Segundos de CPU consumidos en el intervalo
            _cpuSampleUtc = now; // Actualiza la muestra de tiempo
            _cpuTotal = total; // Actualiza la base de CPU

            if (elapsedSec <= 0) // Evita la división por cero
                return 0; // Devuelve 0 si no hubo intervalo
            return Math.Clamp(deltaSec / elapsedSec * 100.0, 0, Environment.ProcessorCount * 100.0); // Porcentaje de CPU acotado al máximo posible
        }
        catch
        {
            return 0; // Ante cualquier error devuelve 0
        }
    }

    public (string Name, int Width, int Height) GetGpuInfo()
    {
        var now = DateTimeOffset.UtcNow; // Hora actual para validar la caché
        if (now < _gpuCacheUntil) // Si la caché sigue vigente
            return (_gpuName, _resolutionWidth, _resolutionHeight); // Devuelve los valores cacheados
        _gpuCacheUntil = now + CacheTtl; // Extiende la validez de la caché

        if (!OperatingSystem.IsWindows()) // WMI solo está disponible en Windows
            return (_gpuName, _resolutionWidth, _resolutionHeight); // Devuelve los valores conocidos

        try
        {
            using var searcher = new ManagementObjectSearcher( // Prepara la consulta WMI a los controladores de vídeo
                "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");
            foreach (var obj in searcher.Get()) // Recorre los controladores de vídeo encontrados
            {
                using var mo = (ManagementObject)obj; // Convierte el objeto WMI a su tipo concreto
                string name = Convert.ToString(mo["Name"]) ?? ""; // Lee el nombre del controlador de vídeo
                if (name.Length == 0) // Ignora entradas sin nombre
                    continue; // Salta al siguiente controlador

                _gpuName = name; // Guarda el nombre de la GPU
                int w = Convert.ToInt32(mo["CurrentHorizontalResolution"]); // Lee la resolución horizontal
                int h = Convert.ToInt32(mo["CurrentVerticalResolution"]); // Lee la resolución vertical
                if (w > 0 && h > 0) // Solo actualiza si la resolución es válida
                {
                    _resolutionWidth = w; // Guarda el ancho de pantalla
                    _resolutionHeight = h; // Guarda el alto de pantalla
                }
                break; // Usa solo el primer controlador válido
            }
        }
        catch
        {
            // WMI no disponible: se conservan los valores anteriores
        }

        return (_gpuName, _resolutionWidth, _resolutionHeight); // Devuelve los valores actuales de GPU
    }

    public double GetTemperatureC()
    {
        var now = DateTimeOffset.UtcNow; // Hora actual para validar la caché
        if (now < _tempCacheUntil) // Si la caché sigue vigente
            return _temperatureC; // Devuelve la temperatura cacheada
        _tempCacheUntil = now + CacheTtl; // Extiende la validez de la caché

        if (!OperatingSystem.IsWindows()) // El sensor térmico ACPI solo se lee en Windows
            return _temperatureC; // Devuelve el valor conocido

        try
        {
            using var searcher = new ManagementObjectSearcher( // Prepara la consulta WMI a la zona térmica ACPI
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var obj in searcher.Get()) // Recorre las zonas térmicas expuestas
            {
                using var mo = (ManagementObject)obj; // Convierte el objeto WMI a su tipo concreto
                var raw = mo["CurrentTemperature"]; // Lee el valor bruto de temperatura
                if (raw is not null) // Solo procesa si hay dato
                {
                    double deciKelvin = Convert.ToDouble(raw); // WMI devuelve décimas de kelvin
                    _temperatureC = Math.Round(deciKelvin / 10.0 - 273.15, 1); // Convierte a grados Celsius redondeando a 1 decimal
                    break; // Usa la primera zona que reporte temperatura
                }
            }
        }
        catch
        {
            // Sensor térmico no expuesto (habitual en equipos sin ACPI): se deja en -1
        }

        return _temperatureC; // Devuelve la temperatura medida o -1
    }

    public double GetGpuUsagePercent()
    {
        var now = DateTimeOffset.UtcNow; // Hora actual para validar la caché
        if (now < _gpuUsageCacheUntil) // Si la caché sigue vigente
            return _gpuUsage; // Devuelve el uso cacheado
        _gpuUsageCacheUntil = now + CacheTtl; // Extiende la validez de la caché

        if (!OperatingSystem.IsWindows()) // nvidia-smi solo está disponible en Windows
            return _gpuUsage; // Devuelve el valor conocido

        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", // Prepara el proceso que consulta el uso de GPU
                "--query-gpu=utilization.gpu --format=csv,noheader,nounits")
            {
                UseShellExecute = false, // No pasa por el shell del sistema
                CreateNoWindow = true, // No crea ventana de consola
                RedirectStandardOutput = true, // Captura la salida estándar
                RedirectStandardError = true // Captura los errores estándar
            };
            using var proc = Process.Start(psi); // Lanza nvidia-smi
            if (proc is null) // Si el proceso no arrancó
                return _gpuUsage; // Devuelve el valor conocido
            string output = proc.StandardOutput.ReadToEnd(); // Lee toda la salida de nvidia-smi
            proc.WaitForExit(2000); // Espera hasta 2 segundos a que termine

            var line = output.Split('\n').FirstOrDefault()?.Trim(); // Toma la primera línea de la salida y la limpia
            if (int.TryParse(line, out int usage)) // Si la línea es un número válido
                _gpuUsage = usage; // Guarda el porcentaje de uso
        }
        catch
        {
            // nvidia-smi no disponible (GPU no NVIDIA): se deja en -1
        }

        return _gpuUsage; // Devuelve el uso medido o -1
    }
}
