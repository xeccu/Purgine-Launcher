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
