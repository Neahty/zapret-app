using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Zapret.Core.ViewModels;

namespace Zapret.App;

public partial class MainWindow : Window
{
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowCorners.Round(this);
        Loaded += (_, _) => PlayEntrance(includeChrome: true);
        // Окно достали из трея - экран снова «собирается», как при открытии.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true && IsLoaded) PlayEntrance(includeChrome: false); };
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as MainViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) PlayEntrance(includeChrome: false);
    }

    private void PlayEntrance(bool includeChrome)
    {
        // После раскладки: строкам заголовка нужна их настоящая высота.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (includeChrome)
            {
                Reveal.Play(Sidebar);
                Reveal.Play(Header);
            }
            var page = _vm?.CurrentPage switch
            {
                AppPage.Strategies => (FrameworkElement)StrategiesPage,
                AppPage.Settings => SettingsPage,
                _ => HomePage,
            };
            Reveal.Play(page);
        });
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("explorer.exe", App.InstallRoot) { UseShellExecute = true });
}
