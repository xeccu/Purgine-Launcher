namespace Purgine;

/// <summary>Formateo legible de duraciones y fechas para la UI.</summary>
public static class Formatting
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        return t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min";
    }

    /// <summary>
    /// Tamaño en la unidad que le corresponde, nunca redondeado a cero: «4 bytes», «12 KB», «350 MB», «1,50 GB»,
    /// «1,25 TB» (con decimales cuando el número es pequeño, para que se vea la cifra real).
    /// </summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024, Tb = Gb * 1024;
        return bytes switch
        {
            >= (long)Tb => $"{bytes / Tb:0.00} TB",
            >= (long)Gb => $"{bytes / Gb:0.00} GB",
            >= (long)Mb => bytes < 10 * Mb ? $"{bytes / Mb:0.0} MB" : $"{bytes / Mb:0} MB",
            >= (long)Kb => bytes < 10 * Kb ? $"{bytes / Kb:0.0} KB" : $"{bytes / Kb:0} KB",
            1 => "1 byte",
            _ => $"{bytes} bytes",
        };
    }

    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond >= 1 << 20 ? $"{bytesPerSecond / (1 << 20):0.0} MB/s" : $"{bytesPerSecond / 1024:0} KB/s";

    public static string Eta(TimeSpan t) =>
        t.TotalMinutes < 1 ? "menos de 1 min"
        : t.TotalHours < 1 ? $"{(int)Math.Ceiling(t.TotalMinutes)} min"
        : $"{(int)t.TotalHours} h {t.Minutes} min";

    public static string RelativeDate(DateTimeOffset? utc)
    {
        if (utc is null) return string.Empty;
        var local = utc.Value.ToLocalTime().Date;
        var days = (DateTime.Today - local).Days;
        return days switch
        {
            0 => "hoy",
            1 => "ayer",
            < 7 => $"hace {days} días",
            _ => local.ToString("d MMM yyyy"),
        };
    }
}
