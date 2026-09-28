// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Shared :: ExternalTools
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Diagnostics; // Proporciona Process y ProcessStartInfo para lanzar procesos
using System.IO; // Proporciona File.Exists y operaciones de rutas

namespace Fedo.StageLnk.Shared; // Espacio de nombres compartido del proyecto

/// <summary>
/// Resuelve y valida la disponibilidad de herramientas externas (ffmpeg, ffprobe, ffplay)
/// mediante variables de entorno personalizadas o búsqueda en PATH.
/// </summary>
public static class ExternalTools // Utilidades de localización de herramientas externas
{
    private static readonly object Gate = new(); // Candado para serializar la resolución
    private static string? _ffmpegPath; // Ruta resuelta de ffmpeg
    private static string? _ffprobePath; // Ruta resuelta de ffprobe
    private static string? _ffplayPath; // Ruta resuelta de ffplay
    private static bool _checkedFfmpeg; // Indica si ya se intentó resolver ffmpeg
    private static bool _checkedFfprobe; // Indica si ya se intentó resolver ffprobe
    private static bool _checkedFfplay; // Indica si ya se intentó resolver ffplay

    /// <summary>Indica si ffmpeg está disponible en el sistema.</summary>
    public static bool IsFfmpegAvailable // Disponibilidad de ffmpeg
    {
        get => ResolveFfmpeg(); // Resuelve y devuelve disponibilidad
    }

    /// <summary>Indica si ffprobe está disponible en el sistema.</summary>
    public static bool IsFfprobeAvailable // Disponibilidad de ffprobe
    {
        get => ResolveFfprobe(); // Resuelve y devuelve disponibilidad
    }

    /// <summary>Indica si ffplay está disponible en el sistema.</summary>
    public static bool IsFfplayAvailable // Disponibilidad de ffplay
    {
        get => ResolveFfplay(); // Resuelve y devuelve disponibilidad
    }

    /// <summary>Devuelve la ruta resuelta de ffmpeg, o null si no está disponible.</summary>
    public static string? FfmpegPath // Ruta de ffmpeg (null si no disponible)
    {
        get
        {
            ResolveFfmpeg();
            return _ffmpegPath;
        }
    }

    /// <summary>Devuelve la ruta resuelta de ffprobe, o null si no está disponible.</summary>
    public static string? FfprobePath // Ruta de ffprobe (null si no disponible)
    {
        get
        {
            ResolveFfprobe();
            return _ffprobePath;
        }
    }

    /// <summary>Devuelve la ruta resuelta de ffplay, o null si no está disponible.</summary>
    public static string? FfplayPath // Ruta de ffplay (null si no disponible)
    {
        get
        {
            ResolveFfplay();
            return _ffplayPath;
        }
    }

    /// <summary>Comprueba la disponibilidad de todas las herramientas y devuelve un resumen.</summary>
    public static (bool Ffmpeg, bool Ffprobe, bool Ffplay) CheckAll() // Comprueba las tres herramientas
    {
        return (ResolveFfmpeg(), ResolveFfprobe(), ResolveFfplay());
    }

    private static bool ResolveFfmpeg() // Localiza ffmpeg (variable de entorno o PATH) una única vez
    {
        if (_checkedFfmpeg) return _ffmpegPath is not null;

        lock (Gate)
        {
            if (_checkedFfmpeg) return _ffmpegPath is not null;

            var env = Environment.GetEnvironmentVariable("FEDO_FFMPEG");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                _ffmpegPath = env;

            if (_ffmpegPath is null)
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    if (proc is not null)
                    {
                        proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        if (proc.ExitCode == 0)
                            _ffmpegPath = "ffmpeg";
                    }
                }
                catch { /* ignore */ }
            }

            _checkedFfmpeg = true;
            return _ffmpegPath is not null;
        }
    }

    private static bool ResolveFfprobe() // Localiza ffprobe (variable de entorno o PATH) una única vez
    {
        if (_checkedFfprobe) return _ffprobePath is not null;

        lock (Gate)
        {
            if (_checkedFfprobe) return _ffprobePath is not null;

            var env = Environment.GetEnvironmentVariable("FEDO_FFPROBE");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                _ffprobePath = env;

            if (_ffprobePath is null)
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo("ffprobe", "-version")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    if (proc is not null)
                    {
                        proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        if (proc.ExitCode == 0)
                            _ffprobePath = "ffprobe";
                    }
                }
                catch { /* ignore */ }
            }

            _checkedFfprobe = true;
            return _ffprobePath is not null;
        }
    }

    private static bool ResolveFfplay() // Localiza ffplay (variable de entorno o PATH) una única vez
    {
        if (_checkedFfplay) return _ffplayPath is not null;

        lock (Gate)
        {
            if (_checkedFfplay) return _ffplayPath is not null;

            var env = Environment.GetEnvironmentVariable("FEDO_FFPLAY");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                _ffplayPath = env;

            if (_ffplayPath is null)
            {
                try
                {
                    using var proc = Process.Start(new ProcessStartInfo("ffplay", "-version")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    });
                    if (proc is not null)
                    {
                        proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit(5000);
                        if (proc.ExitCode == 0)
                            _ffplayPath = "ffplay";
                    }
                }
                catch { /* ignore */ }
            }

            _checkedFfplay = true;
            return _ffplayPath is not null;
        }
    }
}