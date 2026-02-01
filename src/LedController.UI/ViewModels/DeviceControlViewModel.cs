using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LedController.Core.Interfaces;
using LedController.Core.Models;

namespace LedController.UI.ViewModels;

public sealed partial class DeviceControlViewModel : ViewModelBase
{
    private readonly IBleService _bleService;

    public DeviceControlViewModel(IBleService bleService, LedDevice device)
    {
        _bleService = bleService;
        Device = device ?? throw new ArgumentNullException(nameof(device));

        PresetColors = new List<LedColor>
        {
            new("Piros", "#ff0000"),
            new("Zöld", "#00ff00"),
            new("Kék", "#0000ff"),
            new("Sárga", "#ffff00"),
            new("Cián", "#00ffff"),
            new("Lila", "#800080"),
            new("Narancs", "#ffa500"),
            new("Fehér", "#ffffff")
        };

        brightness = Device.Brightness;
    }

    public LedDevice Device { get; }

    public IReadOnlyList<LedColor> PresetColors { get; }

    public event Action? BackRequested;
    public event Action<LedDevice>? SchedulerRequested;

    [ObservableProperty]
    private int brightness;

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
    private async Task TogglePowerAsync(bool? desiredState)
    {
        var targetOn = desiredState ?? !Device.IsOn;
        try
        {
            if (targetOn)
            {
                var color = Device.CurrentColor ?? PresetColors.FirstOrDefault() ?? LedColor.Off;
                await _bleService.SendCommandAsync(Device, color.ToCommandBytes());
                Device.CurrentColor = color;
                Device.IsOn = true;
            }
            else
            {
                await _bleService.SendCommandAsync(Device, LedColor.OffCommandBytes);
                Device.IsOn = false;
                Device.CurrentColor = LedColor.Off;
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

        try
        {
            await _bleService.SendCommandAsync(Device, color.ToCommandBytes());
            Device.CurrentColor = color;
            Device.IsOn = true;
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
        var clamped = Math.Max(0, Math.Min(100, value));
        var hexValue = clamped.ToString("X2", CultureInfo.InvariantCulture);
        var commandHex = $"7e0001{hexValue}00000000ef";

        try
        {
            await _bleService.SendCommandAsync(Device, HexToBytes(commandHex));
            Device.Brightness = clamped;
        }
        catch (Exception ex)
        {
            Device.IsConnected = false;
            Console.WriteLine($"[DeviceControl] Brightness command failed: {ex.Message}");
        }
    }

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
}
