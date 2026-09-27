// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Protocol :: Cue
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Protocol; // espacio de nombres raíz del proyecto de protocolo

/// <summary>
/// Clases de cues soportadas por el sistema de show: medios audiovisuales,
/// texto (anuncios), shaders/efectos, negros, congelados y transiciones.
/// La lista es extensible sin romper el protocolo.
/// </summary>
public enum CueKind // clase o tipo de cue soportada por el sistema de show
{
    Video, // vídeo
    Image, // imagen
    Text, // texto (anuncios)
    Shader, // shader o efecto
    Audio, // audio
    VisualFx, // efecto visual
    Black, // negro (sin señal)
    Freeze, // imagen congelada
    Crossfade, // transición cruzada entre cues
    Fade, // fundido (a negro o desde negro)
    Timer, // temporizador
    Trigger // disparador externo
}

/// <summary>
/// Define una capa visual de un cue: qué medio reproduce, en qué orden de
/// apilado (Z), y con qué transformación/mezcla (posición, escala, rotación,
/// blend, color, brillo, contraste y opacidad).
/// </summary>
public sealed record LayerSpec // registro inmutable que define una capa visual de un cue
{
    public int ZOrder { get; init; } // orden de apilado de la capa (mayor = más delante)
    public string? MediaId { get; init; } // identificador del medio a reproducir (opcional)
    public double X { get; init; } // posición horizontal de la capa en el lienzo
    public double Y { get; init; } // posición vertical de la capa en el lienzo
    public double Scale { get; init; } = 1.0; // escala de la capa (1.0 = tamaño original)
    public double Rotation { get; init; } // rotación de la capa en grados
    public string BlendMode { get; init; } = "Normal"; // modo de mezcla de la capa
    public string Color { get; init; } = "#FFFFFF"; // color en formato hex (#RRGGBB)
    public double Brightness { get; init; } = 1.0; // brillo de la capa (1.0 = sin cambio)
    public double Contrast { get; init; } = 1.0; // contraste de la capa (1.0 = sin cambio)
    public double Opacity { get; init; } = 1.0; // opacidad de la capa (1.0 = totalmente visible)
}

/// <summary>
/// Una cue de la cue list del show: número, nombre, clase, duración opcional,
/// texto (para anuncios), shader, fades de entrada/salida, disparador y sus
/// capas visuales. El número identifica la cue de forma única.
/// </summary>
public sealed record Cue // registro inmutable que representa una cue de la cue list
{
    public int Number { get; init; } // número único que identifica la cue
    public required string Name { get; init; } // nombre de la cue (obligatorio)
    public CueKind Kind { get; init; } // clase de la cue
    public double? Duration { get; init; } // duración opcional en segundos
    public string? Text { get; init; } // texto de la cue (para anuncios)
    public string? ShaderPath { get; init; } // ruta del shader (para efectos)
    public double FadeIn { get; init; } // duración del fundido de entrada en segundos
    public double FadeOut { get; init; } // duración del fundido de salida en segundos
    public string? Trigger { get; init; } // disparador opcional de la cue
    public List<LayerSpec> Layers { get; init; } = new(); // capas visuales que componen la cue
}
