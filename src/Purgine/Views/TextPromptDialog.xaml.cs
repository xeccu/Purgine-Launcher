using System.Windows;
using Purgine.Infrastructure;

namespace Purgine.Views;

/// <summary>Diálogo mínimo para pedir un texto (p. ej. el nombre de una etiqueta).</summary>
public partial class TextPromptDialog : Window
{
    private TextPromptDialog(string title, string message, string initial)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        Title = title;
        MessageText.Text = message;
        InputBox.Text = initial;
        Loaded += (_, _) => { InputBox.Focus(); InputBox.SelectAll(); };
    }

    public string Value => InputBox.Text.Trim();

    /// <summary>Muestra el diálogo; devuelve null si se cancela o se deja vacío.</summary>
    public static string? Ask(Window? owner, string title, string message, string initial = "")
    {
        var dialog = new TextPromptDialog(title, message, initial) { Owner = owner };
        return dialog.ShowDialog() == true && dialog.Value.Length > 0 ? dialog.Value : null;
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}
