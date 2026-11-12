using System.Reflection;

namespace Purgine.Infrastructure;

/// <summary>
/// Versión semántica (<c>1.2.3</c>, <c>0.1.0-alpha</c>, <c>v0.2.0-beta.2</c>). Una preversión es menor que la versión
/// final (<c>0.1.0-alpha &lt; 0.1.0</c>) y sus identificadores se comparan por partes (<c>alpha.2 &lt; alpha.10 &lt; beta</c>).
/// </summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<SemVersion>
{
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : string.Empty;
        var parts = core.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var nums = new int[3];
        for (var i = 0; i < Math.Min(parts.Length, 3); i++)
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return false;
        version = new SemVersion(nums[0], nums[1], nums[2], pre);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return (Prerelease.Length == 0 ? 1 : 0) - (other.Prerelease.Length == 0 ? 1 : 0);

        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], out var ai);
            var bn = int.TryParse(b[i], out var bi);
            c = (an, bn) switch
            {
                (true, true) => ai.CompareTo(bi),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i].ToLowerInvariant(), b[i].ToLowerInvariant()),
            };
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator >(SemVersion l, SemVersion r) => l.CompareTo(r) > 0;
    public static bool operator <(SemVersion l, SemVersion r) => l.CompareTo(r) < 0;

    public override string ToString() => Prerelease.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}

/// <summary>Versión de esta compilación de Purgine (de <c>Directory.Build.props</c>).</summary>
public static class AppVersion
{
    /// <summary>Texto para mostrar, p. ej. «0.1.0-alpha».</summary>
    public static string Display { get; } = Read();

    public static SemVersion Current { get; } = SemVersion.TryParse(Display, out var v) ? v : default;

    private static string Read()
    {
        var asm = typeof(AppVersion).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVersion.TryParse(info, out var v)) return v.ToString();
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
