namespace LightLauncher;

/// <summary>Formateo legible de duraciones y fechas para la UI.</summary>
public static class Formatting
{
    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        return t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min";
    }

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

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
