using System.Windows;
using Purgine.Infrastructure;
using Purgine.ViewModels;

namespace Purgine.Views;

/// <summary>Propiedades de un juego: DLC y mods de Workshop.</summary>
public partial class GamePropertiesDialog : Window
{
    private GamePropertiesDialog(GamePropertiesViewModel vm)
    {
        InitializeComponent();
        WindowTheming.UseDarkTitleBar(this);
        DataContext = vm;
        Loaded += async (_, _) => await vm.LoadAsync();
    }

    public static void Show(Window? owner, GamePropertiesViewModel vm) =>
        new GamePropertiesDialog(vm) { Owner = owner }.ShowDialog();
}
