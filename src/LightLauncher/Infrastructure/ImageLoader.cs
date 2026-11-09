using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightLauncher.Infrastructure;

/// <summary>
/// Carga de imágenes con bajo consumo: se decodifican a la anchura que se va a mostrar
/// (un JPG de 600x900 a 200 px ocupa ~9 veces menos RAM) y se congelan para poder crearlas
/// fuera del hilo de UI.
/// </summary>
public static class ImageLoader
{
    public static BitmapImage? FromFile(string path, int decodeWidth)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return FromStream(stream, decodeWidth);
        }
        catch (Exception)
        {
            return null; // imagen corrupta o en uso: se muestra el placeholder
        }
    }

    public static BitmapImage? FromBytes(byte[] data, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return FromStream(stream, decodeWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static BitmapImage FromStream(Stream stream, int decodeWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;   // lee todo y suelta el stream
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bmp.DecodePixelWidth = decodeWidth;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();                                  // usable desde el hilo de UI
        return bmp;
    }

    /// <summary>Icono grande (32 px) embebido en un .exe, como ImageSource. Null si no tiene.</summary>
    public static ImageSource? IconFromExecutable(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        var large = new IntPtr[1];
        try
        {
            if (NativeMethods.ExtractIconEx(exePath, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero)
                return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (large[0] != IntPtr.Zero) NativeMethods.DestroyIcon(large[0]);
        }
    }
}
