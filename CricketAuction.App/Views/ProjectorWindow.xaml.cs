using System.Windows;
using System.Windows.Input;
namespace CricketAuction.App.Views;
public partial class ProjectorWindow : Window
{
    public ProjectorWindow() => InitializeComponent();
    private void OnKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); if (e.Key == Key.F11) { var full = WindowStyle != WindowStyle.None; WindowStyle = full ? WindowStyle.None : WindowStyle.SingleBorderWindow; WindowState = full ? WindowState.Maximized : WindowState.Normal; } }
}
