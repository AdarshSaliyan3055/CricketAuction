using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using CricketAuction.App.ViewModels;
namespace CricketAuction.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel vm;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent(); vm = viewModel; DataContext = vm;
        Loaded += async (_, _) => { if (!Environment.GetCommandLineArgs().Contains("--smoke-test")) await vm.Initialize(); SearchBox.Focus(); };
        Closing += (_, e) => { if (vm.Busy) { MessageBox.Show(this, "Please wait for the current operation to finish. You can cancel photo downloads."); e.Cancel = true; } };
    }
    private void Navigate(object sender, RoutedEventArgs e) { vm.Section = ((Button)sender).Tag.ToString()!; SearchBox.Focus(); }
    private void OpenPlayer(object sender, MouseButtonEventArgs e) { if (vm.Selected != null) SoldButton.Focus(); }
    private static void Run(ICommand command) { if (command.CanExecute(null)) command.Execute(null); }
    private void LoaderVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) Keyboard.Focus((Grid)sender);
        else if (IsLoaded) SearchBox.Focus();
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (vm.Busy)
        {
            if (e.Key == Key.Escape) { Run(vm.CancelPhotosCommand); e.Handled = true; }
            else e.Handled = e.Key is not (Key.Tab or Key.Enter or Key.Space);
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.E) { Run(vm.ExportCommand); e.Handled = true; }
            else if (e.Key == Key.P) { Run(vm.PptCommand); e.Handled = true; }
            return;
        }
        if (e.Key == Key.F5) { Run(vm.RefreshCommand); e.Handled = true; return; }
        if (e.Key == Key.Escape) { vm.Selected = null; SearchBox.Focus(); e.Handled = true; return; }
        if (e.Key == Key.Enter && (SearchBox.IsKeyboardFocusWithin || PlayerGrid.IsKeyboardFocusWithin))
        {
            SearchBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            if (vm.Selected == null || !vm.Players.Any(p => p.Id == vm.Selected.Id)) vm.Selected = vm.Players.FirstOrDefault();
            if (vm.Selected != null) SoldButton.Focus(); e.Handled = true; return;
        }
        if (Keyboard.FocusedElement is TextBoxBase or ComboBox || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.S) { Run(vm.SoldCommand); e.Handled = true; }
        if (e.Key == Key.U) { Run(vm.UnsoldCommand); e.Handled = true; }
    }
}
