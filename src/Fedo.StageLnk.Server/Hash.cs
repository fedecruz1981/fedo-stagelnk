// =============================================================================
// Fedo-StageLnk v1.0 — fedo-soft
// Archivo: Fedo.StageLnk.Server :: Hash
// © 2026 fedo-soft. Todos los derechos reservados.
// =============================================================================

using System.Security.Cryptography; // Proporciona las clases criptográficas (SHA256)

namespace Fedo.StageLnk.Server; // Espacio de nombres del servidor

/// <summary>Utilidades de hash para verificar la integridad de los archivos.</summary>
public static class Hash // Clase estática de utilidades de hash
{
    /// <summary>Calcula el SHA-256 (hexadecimal en minúsculas) de un archivo.</summary>
    public static string Sha256File(string path) // Calcula el hash SHA-256 de un archivo
    {
        using var sha = SHA256.Create(); // Crea una instancia del algoritmo SHA-256
        using var stream = File.OpenRead(path); // Abre el archivo en modo lectura
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant(); // Calcula el hash y lo devuelve en hexadecimal minúsculas
    }
}
