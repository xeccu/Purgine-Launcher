using System.Windows;
using System.Windows.Controls;
using Purgine.Infrastructure;
using Purgine.Library;
using Microsoft.Win32;

namespace Purgine.Views;

/// <summary>Alta / edición de un ejecutable local (nombre, ruta, argumentos, portada).</summary>
public partial class LocalGameDialog : Window
{
    private readonly UserLibrary _library;
    private readonly LocalGameDefinition _game;
    private readonly string? _originalCover;
    private string? _coverSource; // ruta elegida (aún sin importar) o la portada actual
    private bool _nameEditedByUser;

    private LocalGameDialog(UserLibrary library, LocalGameDefinition? existing)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        _library = library;

        // Trabajamos sobre una copia: cancelar no debe modificar nada.
        _game = existing is null
            ? new LocalGameDefinition()
            : new LocalGameDefinition
            {
                Id = existing.Id,
                Name = existing.Name,
                ExecutablePath = existing.ExecutablePath,
                Arguments = existing.Arguments,
                WorkingDirectory = existing.WorkingDirectory,
                CoverImagePath = existing.CoverImagePath,
                AddedUtc = existing.AddedUtc,
            };
        _originalCover = existing?.CoverImagePath;

        if (existing is not null)
        {
            Title = $"Editar «{existing.Name}»";
            SaveButton.Content = "Guardar cambios";
            ExeBox.Text = existing.ExecutablePath;
            NameBox.Text = existing.Name;
            ArgsBox.Text = existing.Arguments ?? string.Empty;
            WorkDirBox.Text = existing.WorkingDirectory ?? string.Empty;
            _nameEditedByUser = true;
            SetCover(existing.CoverImagePath);
        }

        NameBox.TextChanged += (_, _) => { if (NameBox.IsKeyboardFocused) _nameEditedByUser = true; };
        Loaded += (_, _) =>
        {
            if (existing is null) OnBrowseExe(this, new RoutedEventArgs()); // alta: abrir directamente el selector
            else NameBox.Focus();
        };
    }

    /// <summary>Muestra el diálogo. Devuelve la definición guardada o null si se cancela.</summary>
    public static LocalGameDefinition? Show(Window? owner, UserLibrary library, LocalGameDefinition? existing)
    {
        var dialog = new LocalGameDialog(library, existing) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._game : null;
    }

    private void OnBrowseExe(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Elige el ejecutable del juego",
            Filter = "Ejecutables (*.exe;*.bat;*.cmd;*.lnk)|*.exe;*.bat;*.cmd;*.lnk|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            DereferenceLinks = false,
        };
        if (File.Exists(ExeBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(ExeBox.Text);
        if (dlg.ShowDialog(this) == true) ExeBox.Text = dlg.FileName;
    }

    private void OnExeChanged(object sender, TextChangedEventArgs e)
    {
        var path = ExeBox.Text.Trim().Trim('"');
        if (!File.Exists(path)) { IconPreview.Source = null; return; }

        IconPreview.Source = ImageLoader.IconFromExecutable(path);
        if (!_nameEditedByUser)
            NameBox.Text = Prettify(Path.GetFileNameWithoutExtension(path));
    }

    private void OnBrowseWorkDir(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Carpeta de trabajo" };
        if (Directory.Exists(WorkDirBox.Text)) dlg.InitialDirectory = WorkDirBox.Text;
        if (dlg.ShowDialog(this) == true) WorkDirBox.Text = dlg.FolderName;
    }

    private void OnBrowseCover(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Elige una portada",
            Filter = "Imágenes (*.jpg;*.jpeg;*.png;*.bmp;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) == true) SetCover(dlg.FileName);
    }

    private void OnClearCover(object sender, RoutedEventArgs e) => SetCover(null);

    private void SetCover(string? path)
    {
        var image = path is null ? null : ImageLoader.FromFile(path, 280);
        _coverSource = image is null ? null : path;
        CoverBrush.ImageSource = image;
        CoverBorder.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        ClearCoverButton.Visibility = CoverBorder.Visibility;
        if (path is not null && image is null) ShowError("No se pudo leer esa imagen.");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var exe = ExeBox.Text.Trim().Trim('"');
        var name = NameBox.Text.Trim();
        var workDir = WorkDirBox.Text.Trim().Trim('"');

        if (!File.Exists(exe)) { ShowError("El ejecutable no existe. Usa «Examinar…» para elegirlo."); return; }
        if (name.Length == 0) { ShowError("Ponle un nombre."); return; }
        if (workDir.Length > 0 && !Directory.Exists(workDir)) { ShowError("La carpeta de trabajo no existe."); return; }

        _game.ExecutablePath = Path.GetFullPath(exe);
        _game.Name = name;
        _game.Arguments = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim();
        _game.WorkingDirectory = workDir.Length == 0 ? null : workDir;

        // Portada: se copia a la carpeta de datos para que no dependa del archivo original.
        if (_coverSource != _originalCover)
        {
            _game.CoverImagePath = _coverSource is null ? null : _library.ImportCover(_coverSource, _game.Id);
            _library.DeleteCoverIfOwned(_originalCover);
        }

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>"my_cool-game" → "My cool game" (nombre inicial razonable a partir del .exe).</summary>
    private static string Prettify(string fileName)
    {
        var s = fileName.Replace('_', ' ').Replace('-', ' ').Trim();
        return s.Length == 0 ? fileName : char.ToUpper(s[0]) + s[1..];
    }
}
