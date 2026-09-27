// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Client :: Fft
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

namespace Fedo.StageLnk.Client;

/// <summary>FFT radix-2 iterativa mínima para el análisis de espectro en vivo.</summary>
// Clase estática con utilidades de transformada rápida de Fourier.
public static class Fft
{
    /// <summary>Devuelve la magnitud normalizada por bin para la mitad positiva del espectro.</summary>
    // Calcula las magnitudes espectrales a partir de muestras de coma flotante simple.
    public static double[] Magnitudes(ReadOnlySpan<float> samples)
    {
        // Buffer real con las muestras convertidas a doble precisión.
        var re = new double[samples.Length];
        // Copia cada muestra al buffer real.
        for (int i = 0; i < samples.Length; i++)
            re[i] = samples[i];
        // Delega en la versión con entradas en doble precisión.
        return Magnitudes(re);
    }

    /// <summary>Devuelve la magnitud normalizada por bin para la mitad positiva del espectro.</summary>
    // Calcula las magnitudes espectrales a partir de muestras en doble precisión.
    public static double[] Magnitudes(ReadOnlySpan<double> samples)
    {
        // Potencia de dos mínima que cubre el número de muestras.
        int n = 1;
        // Duplica n hasta igualar o superar la longitud de la entrada.
        while (n < samples.Length)
            n <<= 1;

        // Parte real del buffer de la FFT, rellenada a tamaño potencia de dos.
        var re = new double[n];
        // Parte imaginaria del buffer de la FFT (ceros iniciales).
        var im = new double[n];
        // Copia las muestras de entrada al buffer real hasta llenarlo.
        for (int i = 0; i < samples.Length && i < n; i++)
            re[i] = samples[i];

        // Aplica la transformada in-place sobre re e im.
        Transform(re, im);

        // Buffer de magnitudes para la mitad positiva del espectro.
        var mag = new double[n / 2];
        // Calcula la magnitud (módulo) normalizada de cada bin.
        for (int i = 0; i < n / 2; i++)
            mag[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]) / n;
        return mag;
    }

    // FFT radix-2 iterativa in-place sobre los buffers real e imaginario.
    private static void Transform(double[] re, double[] im)
    {
        // Tamaño del problema (potencia de dos).
        int n = re.Length;

        // Índice auxiliar para el reordenamiento de bit-reversal.
        int j = 0;
        // Recorre las posiciones para desordenar la entrada en orden de bits invertidos.
        for (int i = 1; i < n; i++)
        {
            // Bit más significativo del tamaño actual.
            int bit = n >> 1;
            // Invierte los bits de j de forma iterativa.
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            // Solo intercambia una vez cada pareja (cuando i < j).
            if (i < j)
            {
                // Intercambia la parte real de las posiciones i y j.
                (re[i], re[j]) = (re[j], re[i]);
                // Intercambia la parte imaginaria de las posiciones i y j.
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // Itera por tamaños de mariposa: 2, 4, 8... hasta n.
        for (int len = 2; len <= n; len <<= 1)
        {
            // Ángulo base de la raíz de la unidad para este tamaño.
            double angle = -2.0 * Math.PI / len;
            // Coseno del ángulo (parte real del twiddle).
            double wRe = Math.Cos(angle);
            // Seno del ángulo (parte imaginaria del twiddle).
            double wIm = Math.Sin(angle);

            // Procesa cada bloque de longitud `len` por separado.
            for (int i = 0; i < n; i += len)
            {
                // Parte real del factor de giro actual, parte real unitaria.
                double curRe = 1.0;
                // Parte imaginaria del factor de giro actual (cero inicial).
                double curIm = 0.0;
                // Mitad del bloque, distancia entre elementos emparejados.
                int half = len / 2;

                // Aplica la mariposa a cada par de elementos del bloque.
                for (int k = 0; k < half; k++)
                {
                    // Índice del elemento inferior del par.
                    int a = i + k;
                    // Índice del elemento superior del par.
                    int b = i + k + half;

                    // Parte real del producto del twiddle por el elemento superior.
                    double tRe = curRe * re[b] - curIm * im[b];
                    // Parte imaginaria del producto del twiddle por el elemento superior.
                    double tIm = curRe * im[b] + curIm * re[b];

                    // Parte real del superior pasa a ser a - t.
                    re[b] = re[a] - tRe;
                    // Parte imaginaria del superior pasa a ser a - t.
                    im[b] = im[a] - tIm;
                    // El inferior acumula el término positivo (a + t).
                    re[a] += tRe;
                    // El inferior acumula el término imaginario positivo (a + t).
                    im[a] += tIm;

                    // Rota el factor de giro para el siguiente par del bloque.
                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }
    }
}
