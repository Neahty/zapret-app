using System.ComponentModel;
using System.Reflection;
using Zapret.Core.ViewModels;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Zapret.App;

/// <summary>Значок в трее: показывает, включён ли обход, и даёт включить/выключить его без окна.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Drawing.Icon _onIcon = LoadIcon("app.ico");
    private readonly Drawing.Icon _offIcon = LoadIcon("app-off.ico");

    public TrayIcon(MainViewModel vm, Action showWindow, Action exit)
    {
        _vm = vm;
        _toggleItem = new Forms.ToolStripMenuItem("Выключить", null, async (_, _) => await _vm.ToggleCommand.ExecuteAsync(null));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Открыть", null, (_, _) => showWindow()) { Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold) });
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Выход (обход продолжит работать)", null, (_, _) => exit()));

        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) showWindow(); };

        _vm.PropertyChanged += OnVmChanged;
        Update();
    }

    public void ShowHint(string text) => _icon.ShowBalloonTip(3000, "Запрет", text, Forms.ToolTipIcon.Info);

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsOn) or nameof(MainViewModel.IsBusy) or nameof(MainViewModel.SelectedStrategy))
            Update();
    }

    private void Update()
    {
        _icon.Icon = _vm.IsOn ? _onIcon : _offIcon;
        var text = $"Запрет — {(_vm.IsOn ? "включён" : "выключен")}";
        if (_vm.IsOn && _vm.SelectedStrategy is { } s) text += $"\n{s.Name}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
        _toggleItem.Text = _vm.IsOn ? "Выключить обход" : "Включить обход";
        _toggleItem.Enabled = !_vm.IsBusy;
    }

    private static Drawing.Icon LoadIcon(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Нет ресурса {name}");
        return new Drawing.Icon(stream, new Drawing.Size(16, 16));
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnVmChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _onIcon.Dispose();
        _offIcon.Dispose();
    }
}
