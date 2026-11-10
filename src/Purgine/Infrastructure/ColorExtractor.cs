using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Purgine.Infrastructure;

/// <summary>Color dominante «vivo» de una imagen (para el brillo de la ficha en el perfil Ultra-moderno).</summary>
public static class ColorExtractor
{
    /// <summary>
    /// Muestrea una rejilla de ~2.000 píxeles, los agrupa por tono (24 sectores) ponderando saturación y brillo,
    /// y devuelve la media del sector más pesado, aclarada para que brille sobre fondo oscuro. Null si la imagen
    /// es casi gris. Seguro desde cualquier hilo si la imagen está congelada.
    /// </summary>
    public static Color? Dominant(BitmapSource? image)
    {
        if (image is null || image.PixelWidth == 0 || image.PixelHeight == 0) return null;
        try
        {
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var stepX = Math.Max(1, w / 56);
            var stepY = Math.Max(1, h / 36);
            var row = new byte[w * 4];

            const int Buckets = 24;
            var weight = new double[Buckets];
            var sumR = new double[Buckets];
            var sumG = new double[Buckets];
            var sumB = new double[Buckets];

            for (var y = 0; y < h; y += stepY)
            {
                bgra.CopyPixels(new System.Windows.Int32Rect(0, y, w, 1), row, row.Length, 0);
                for (var x = 0; x < w; x += stepX)
                {
                    double b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    var max = Math.Max(r, Math.Max(g, b));
                    var min = Math.Min(r, Math.Min(g, b));
                    if (max < 40) continue; // casi negro
                    var sat = (max - min) / max;
                    if (sat < 0.25) continue; // casi gris
                    var hue = Hue(r, g, b, max, min);
                    var bucket = (int)(hue / 360 * Buckets) % Buckets;
                    var wgt = sat * (max / 255.0);
                    weight[bucket] += wgt;
                    sumR[bucket] += r * wgt;
                    sumG[bucket] += g * wgt;
                    sumB[bucket] += b * wgt;
                }
            }

            var best = Array.IndexOf(weight, weight.Max());
            if (weight[best] <= 0) return null;
            var c = Color.FromRgb((byte)(sumR[best] / weight[best]), (byte)(sumG[best] / weight[best]), (byte)(sumB[best] / weight[best]));
            return Brighten(c);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        var d = max - min;
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Lleva el color a un brillo alto manteniendo el tono (un brillo oscuro no se ve).</summary>
    private static Color Brighten(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (max == 0) return c;
        var f = 235.0 / max;
        return Color.FromRgb((byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));
    }
}
