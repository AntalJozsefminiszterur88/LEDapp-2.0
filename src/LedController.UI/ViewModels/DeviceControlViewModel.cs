using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class DeviceControlViewModel : ViewModelBase, IDisposable
{
    private readonly IBleService _bleService;
    private readonly ISchedulerService _schedulerService;
    private readonly IConfigService _configService;
    private readonly DispatcherTimer _customColorTimer;
    private readonly DispatcherTimer _editColorTimer;
    private readonly DispatcherTimer _brightnessSaveTimer;
    private readonly IReadOnlyList<LedColor> _defaultColors;
    private bool _suppressCustomColorUpdate;
    private bool _suppressEditColorUpdate;
    private bool _sendingCustomColor;
    private bool _sendingEditColor;
    private string? _pendingCustomColorHex;
    private string? _pendingEditColorHex;
    private int? _pendingBrightness;

    public DeviceControlViewModel(
        IBleService bleService,
        ISchedulerService schedulerService,
        IConfigService configService,
        LedDevice device)
    {
        _bleService = bleService;
        _schedulerService = schedulerService;
        _configService = configService;
        Device = device ?? throw new ArgumentNullException(nameof(device));

        _defaultColors = CreateDefaultColors().ToList();
        PresetColors = new ObservableCollection<LedColor>(_defaultColors);

        brightness = Device.Brightness;

        IsScheduleEnabled = Device.Profiles.Count == 0 || Device.Profiles.Any(p => p.IsActive);

        _customColorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _customColorTimer.Tick += OnCustomColorTimerTick;

        _editColorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _editColorTimer.Tick += OnEditColorTimerTick;

        _brightnessSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _brightnessSaveTimer.Tick += OnBrightnessSaveTimerTick;

        InitializeCustomColor();
        _ = LoadCustomColorsAsync();
    }

    public LedDevice Device { get; }

    public ObservableCollection<LedColor> PresetColors { get; }

    public event Action? BackRequested;
    public event Action<LedDevice>? SchedulerRequested;

    [ObservableProperty]
    private int brightness;

    [ObservableProperty]
    private bool isScheduleEnabled;

    [ObservableProperty]
    private Color customColor = Colors.White;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCustomColorCommand))]
    private string customColorName = string.Empty;

    [ObservableProperty]
    private Color editColor = Colors.White;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveEditedColorCommand))]
    private string editColorName = string.Empty;

    [ObservableProperty]
    private LedColor? editingColor;

    public string CustomColorHex => $"#{CustomColor.R:X2}{CustomColor.G:X2}{CustomColor.B:X2}".ToLowerInvariant();

    public string EditColorHex => $"#{EditColor.R:X2}{EditColor.G:X2}{EditColor.B:X2}".ToLowerInvariant();

    public string ScheduleButtonLabel => IsScheduleEnabled ? "Ütemezés: Aktív" : "Ütemezés: Inaktív";

    partial void OnIsScheduleEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ScheduleButtonLabel));
        _schedulerService.SetDeviceScheduleEnabled(Device.Id, value);
        _schedulerService.MarkDeviceStateDirty(Device.Id);
    }

    partial void OnCustomColorChanged(Color value)
    {
        OnCustomColorComponentChanged();
    }

    partial void OnEditColorChanged(Color value)
    {
        OnPropertyChanged(nameof(EditColorHex));
        if (_suppressEditColorUpdate)
        {
            return;
        }

        QueueEditColorUpdate();
    }

    partial void OnBrightnessChanged(int value)
    {
        if (SetBrightnessCommand.CanExecute(value))
        {
            SetBrightnessCommand.Execute(value);
        }
    }

    [RelayCommand]
    private void Back()
    {
        BackRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenScheduler()
    {
        SchedulerRequested?.Invoke(Device);
    }

    [RelayCommand]
    private void ToggleSchedule()
    {
        IsScheduleEnabled = !IsScheduleEnabled;
    }

    [RelayCommand(CanExecute = nameof(CanSaveCustomColor))]
    private async Task SaveCustomColorAsync()
    {
        var name = (CustomColorName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var color = new LedColor(name, CustomColorHex);
        AddOrUpdatePresetColor(color);
        await SaveCustomColorsAsync();
        CustomColorName = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSaveEditedColor))]
    private async Task SaveEditedColorAsync()
    {
        var source = EditingColor;
        if (source is null)
        {
            return;
        }

        var name = (EditColorName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var updated = new LedColor(name, EditColorHex);
        ReplacePresetColor(source, updated);
        EditingColor = updated;
        await SaveCustomColorsAsync();
    }

    [RelayCommand]
    private async Task TogglePowerAsync(bool? desiredState)
    {
        if (!await EnsureConnectedAsync())
        {
            return;
        }

        var targetOn = desiredState ?? !Device.IsOn;
        try
        {
            if (targetOn)
            {
                var color = Device.CurrentColor ?? PresetColors.FirstOrDefault() ?? LedColor.Off;
                await _bleService.SendCommandAsync(Device, color.ToCommandBytes());
                Device.CurrentColor = color;
                Device.IsOn = true;
                _schedulerService.MarkDeviceStateDirty(Device.Id);
            }
            else
            {
                await _bleService.SendCommandAsync(Device, LedColor.OffCommandBytes);
                Device.IsOn = false;
                Device.CurrentColor = LedColor.Off;
                _schedulerService.MarkDeviceStateDirty(Device.Id);
            }
        }
        catch (Exception ex)
        {
            Device.IsConnected = false;
            Console.WriteLine($"[DeviceControl] Power command failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetColorAsync(LedColor? color)
    {
        if (color is null)
        {
            return;
        }

        if (!await EnsureConnectedAsync())
        {
            return;
        }

        try
        {
            await _bleService.SendCommandAsync(Device, color.ToCommandBytes());
            Device.CurrentColor = color;
            Device.IsOn = true;
            _schedulerService.MarkDeviceStateDirty(Device.Id);
        }
        catch (Exception ex)
        {
            Device.IsConnected = false;
            Console.WriteLine($"[DeviceControl] Color command failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SetBrightnessAsync(int value)
    {
        if (!await EnsureConnectedAsync())
        {
            return;
        }

        var clamped = Math.Max(0, Math.Min(100, value));
        var hexValue = clamped.ToString("X2", CultureInfo.InvariantCulture);
        var commandHex = $"7e0001{hexValue}00000000ef";

        try
        {
            await _bleService.SendCommandAsync(Device, HexToBytes(commandHex));
            Device.Brightness = clamped;
            QueueBrightnessPersist(clamped);
        }
        catch (Exception ex)
        {
            Device.IsConnected = false;
            Console.WriteLine($"[DeviceControl] Brightness command failed: {ex.Message}");
        }
    }

    private void OnCustomColorComponentChanged()
    {
        OnPropertyChanged(nameof(CustomColorHex));
        if (_suppressCustomColorUpdate)
        {
            return;
        }

        QueueCustomColorUpdate();
    }

    private void QueueCustomColorUpdate()
    {
        _pendingCustomColorHex = CustomColorHex;
        _customColorTimer.Stop();
        _customColorTimer.Start();
    }

    private void QueueEditColorUpdate()
    {
        if (EditingColor is null)
        {
            return;
        }

        _pendingEditColorHex = EditColorHex;
        _editColorTimer.Stop();
        _editColorTimer.Start();
    }

    private void QueueBrightnessPersist(int brightnessValue)
    {
        _pendingBrightness = brightnessValue;
        _brightnessSaveTimer.Stop();
        _brightnessSaveTimer.Start();
    }

    private async Task ApplyCustomColorAsync()
    {
        var hex = _pendingCustomColorHex;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return;
        }

        if (_sendingCustomColor)
        {
            return;
        }

        _sendingCustomColor = true;
        try
        {
            var color = new LedColor("Egyedi", hex);
            await SetColorAsync(color);
        }
        finally
        {
            _sendingCustomColor = false;
            if (!string.Equals(hex, _pendingCustomColorHex, StringComparison.OrdinalIgnoreCase))
            {
                QueueCustomColorUpdate();
            }
        }
    }

    private async Task ApplyEditColorAsync()
    {
        if (EditingColor is null)
        {
            return;
        }

        var hex = _pendingEditColorHex;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return;
        }

        if (_sendingEditColor)
        {
            return;
        }

        _sendingEditColor = true;
        try
        {
            var name = string.IsNullOrWhiteSpace(EditColorName) ? "Egyedi" : EditColorName.Trim();
            var color = new LedColor(name, hex);
            await SetColorAsync(color);
        }
        finally
        {
            _sendingEditColor = false;
            if (!string.Equals(hex, _pendingEditColorHex, StringComparison.OrdinalIgnoreCase))
            {
                QueueEditColorUpdate();
            }
        }
    }

    private async void OnCustomColorTimerTick(object? sender, EventArgs e)
    {
        _customColorTimer.Stop();
        await ApplyCustomColorAsync();
    }

    private async void OnEditColorTimerTick(object? sender, EventArgs e)
    {
        _editColorTimer.Stop();
        await ApplyEditColorAsync();
    }

    private async void OnBrightnessSaveTimerTick(object? sender, EventArgs e)
    {
        _brightnessSaveTimer.Stop();
        await PersistBrightnessAsync();
    }

    private async Task PersistBrightnessAsync()
    {
        var brightnessValue = _pendingBrightness;
        if (!brightnessValue.HasValue)
        {
            return;
        }

        _pendingBrightness = null;
        await SaveDeviceBrightnessAsync(brightnessValue.Value);
    }

    private void InitializeCustomColor()
    {
        var sourceHex = Device.CurrentColor?.Hex ?? PresetColors.FirstOrDefault()?.Hex;
        if (!TryParseHex(sourceHex, out var red, out var green, out var blue))
        {
            red = 255;
            green = 255;
            blue = 255;
        }

        _suppressCustomColorUpdate = true;
        CustomColor = Color.FromRgb((byte)red, (byte)green, (byte)blue);
        _suppressCustomColorUpdate = false;
        OnPropertyChanged(nameof(CustomColorHex));
    }

    private async Task LoadCustomColorsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var settings = config.Settings ?? AppSettings.Default;
            var customColors = settings.CustomColors ?? Array.Empty<LedColor>();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var color in customColors)
                {
                    AddOrUpdatePresetColor(color);
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceControl] Custom colors load failed: {ex.Message}");
        }
    }

    private async Task SaveCustomColorsAsync()
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var settings = config.Settings ?? AppSettings.Default;

            var customColors = PresetColors
                .Where(color => !IsDefaultColor(color))
                .Select(color => new LedColor(color.Name, color.Hex))
                .ToList();

            settings = settings with { CustomColors = customColors };

            var updated = new AppConfig(config.SavedDevices, config.Profiles, settings);
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceControl] Custom colors save failed: {ex.Message}");
        }
    }

    private async Task SaveDeviceBrightnessAsync(int brightnessValue)
    {
        try
        {
            var config = await _configService.LoadConfigAsync();
            var devices = config.SavedDevices.ToList();
            var target = devices.FirstOrDefault(d =>
                d.Id == Device.Id ||
                (!string.IsNullOrWhiteSpace(d.MacAddress) &&
                 string.Equals(d.MacAddress, Device.MacAddress, StringComparison.OrdinalIgnoreCase)));

            if (target is null)
            {
                target = Device;
                devices.Add(target);
            }

            target.Brightness = brightnessValue;

            var updated = new AppConfig(devices, config.Profiles, config.Settings);
            await _configService.SaveConfigAsync(updated);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DeviceControl] Brightness save failed: {ex.Message}");
        }
    }

    private void AddOrUpdatePresetColor(LedColor color)
    {
        var existing = PresetColors.FirstOrDefault(c =>
            string.Equals(c.Name, color.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            existing = PresetColors.FirstOrDefault(c =>
                string.Equals(c.NormalizedHex, color.NormalizedHex, StringComparison.OrdinalIgnoreCase));
        }

        if (existing is not null)
        {
            var index = PresetColors.IndexOf(existing);
            if (index >= 0)
            {
                PresetColors[index] = color;
            }

            return;
        }

        PresetColors.Add(color);
    }

    private void ReplacePresetColor(LedColor original, LedColor updated)
    {
        var index = PresetColors.IndexOf(original);
        if (index >= 0)
        {
            PresetColors[index] = updated;
            return;
        }

        AddOrUpdatePresetColor(updated);
    }

    private bool IsDefaultColor(LedColor color)
    {
        return _defaultColors.Any(defaultColor =>
            string.Equals(defaultColor.Name, color.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(defaultColor.NormalizedHex, color.NormalizedHex, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseHex(string? hex, out int red, out int green, out int blue)
    {
        red = 0;
        green = 0;
        blue = 0;

        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var trimmed = hex.Trim();
        if (trimmed.StartsWith('#'))
        {
            trimmed = trimmed.Substring(1);
        }

        if (trimmed.Length != 6)
        {
            return false;
        }

        if (!int.TryParse(trimmed.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out red))
        {
            return false;
        }

        if (!int.TryParse(trimmed.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out green))
        {
            return false;
        }

        if (!int.TryParse(trimmed.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out blue))
        {
            return false;
        }

        return true;
    }

    private static LedColor[] CreateDefaultColors() => new[]
    {
        new LedColor("Piros", "#ff0000"),
        new LedColor("Zöld", "#00ff00"),
        new LedColor("Kék", "#0000ff"),
        new LedColor("Sárga", "#ffff00"),
        new LedColor("Cián", "#00ffff"),
        new LedColor("Lila", "#800080"),
        new LedColor("Narancs", "#ffa500"),
        new LedColor("Fehér", "#ffffff")
    };

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
        {
            throw new ArgumentException("Hex string must have an even length.", nameof(hex));
        }

        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            var slice = hex.Substring(i * 2, 2);
            bytes[i] = byte.Parse(slice, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        return bytes;
    }

    private async Task<bool> EnsureConnectedAsync()
    {
        if (Device.IsConnected)
        {
            return true;
        }

        try
        {
            await _bleService.ConnectAsync(Device);
            return Device.IsConnected;
        }
        catch (Exception ex)
        {
            Device.IsConnected = false;
            Console.WriteLine($"[DeviceControl] Connect failed: {ex.Message}");
            return false;
        }
    }

    private bool CanSaveCustomColor() => !string.IsNullOrWhiteSpace(CustomColorName);

    private bool CanSaveEditedColor() => EditingColor is not null && !string.IsNullOrWhiteSpace(EditColorName);

    public void BeginEditColor(LedColor color)
    {
        if (color is null)
        {
            return;
        }

        EditingColor = color;
        EditColorName = color.Name;
        SaveEditedColorCommand.NotifyCanExecuteChanged();

        if (!TryParseHex(color.Hex, out var red, out var green, out var blue))
        {
            red = 255;
            green = 255;
            blue = 255;
        }

        _suppressEditColorUpdate = true;
        EditColor = Color.FromRgb((byte)red, (byte)green, (byte)blue);
        _suppressEditColorUpdate = false;
        OnPropertyChanged(nameof(EditColorHex));
    }

    public void CancelEditColor()
    {
        EditingColor = null;
        _pendingEditColorHex = null;
        _editColorTimer.Stop();
        SaveEditedColorCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _customColorTimer.Stop();
        _editColorTimer.Stop();
        _brightnessSaveTimer.Stop();

        _customColorTimer.Tick -= OnCustomColorTimerTick;
        _editColorTimer.Tick -= OnEditColorTimerTick;
        _brightnessSaveTimer.Tick -= OnBrightnessSaveTimerTick;
    }
}






