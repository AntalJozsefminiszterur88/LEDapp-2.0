using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using LedController.Core.Interfaces;
using LedController.Core.Models;
using InTheHand.Bluetooth;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

using InTheHandBluetoothDevice = InTheHand.Bluetooth.BluetoothDevice;
using InTheHandGattCharacteristic = InTheHand.Bluetooth.GattCharacteristic;
using WindowsGattCharacteristic = Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristic;

namespace LedController.Infrastructure.Services;

public sealed class WindowsBleService : IBleService
{
    private static readonly Guid LedCharacteristicUuid = Guid.Parse("0000fff3-0000-1000-8000-00805f9b34fb");
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
    private static int _connectSequence;

    private readonly ConcurrentDictionary<Guid, DeviceConnection> _connections = new();
    private readonly ConcurrentDictionary<Guid, WindowsDeviceConnection> _windowsConnections = new();

    public async Task<IReadOnlyList<LedDevice>> ScanForDevicesAsync()
    {
        BleLog.Info($"Scan start. OS={Environment.OSVersion}; Framework={Environment.Version}; Is64Bit={Environment.Is64BitProcess}.");

        if (OperatingSystem.IsWindows())
        {
            var discovered = new ConcurrentDictionary<string, LedDevice>(StringComparer.OrdinalIgnoreCase);

            void HandleAdvertisement(BluetoothLEAdvertisementWatcher? sender, BluetoothLEAdvertisementReceivedEventArgs args)
            {
                var name = args.Advertisement.LocalName;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }

                var address = FormatBluetoothAddress(args.BluetoothAddress);
                if (string.IsNullOrWhiteSpace(address))
                {
                    return;
                }

                discovered.AddOrUpdate(
                    address,
                    _ => new LedDevice
                    {
                        Name = name,
                        MacAddress = address,
                        DeviceIdentifier = address
                    },
                    (_, existing) =>
                    {
                        if (string.IsNullOrWhiteSpace(existing.Name))
                        {
                            existing.Name = name;
                        }

                        return existing;
                    });
            }

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Active
            };

            try
            {
                BleLog.Info("Windows watcher start.");
                watcher.Received += HandleAdvertisement;
                watcher.Start();
                await Task.Delay(TimeSpan.FromSeconds(12));
            }
            catch (Exception ex)
            {
                BleLog.Exception("Windows scan failed.", ex);
                throw;
            }
            finally
            {
                try
                {
                    watcher.Stop();
                }
                catch (Exception ex)
                {
                    BleLog.Exception("Windows watcher stop failed.", ex);
                }

                watcher.Received -= HandleAdvertisement;
            }

            var results = discovered.Values
                .OrderBy(device => device.Name)
                .ToList();

            BleLog.Info($"Scan complete. Found {results.Count} devices.");
            return results;
        }

        try
        {
            var devices = await Bluetooth.ScanForDevicesAsync();
            var results = devices
                .Where(device => device is not null)
                .GroupBy(device => device.Id)
                .Select(group =>
                {
                    var device = group.First();
                    return new LedDevice
                    {
                        Name = device.Name ?? string.Empty,
                        MacAddress = device.Id
                    };
                })
                .ToList();

            BleLog.Info($"Scan complete (non-Windows). Found {results.Count} devices.");
            return results;
        }
        catch (Exception ex)
        {
            BleLog.Exception("ScanForDevicesAsync failed (non-Windows).", ex);
            throw;
        }
    }

    public async Task ConnectAsync(LedDevice device)
    {
        if (device is null)
        {
            throw new ArgumentNullException(nameof(device));
        }

        var connectionId = LedDeviceIdentity.GetConnectionId(device);
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            throw new InvalidOperationException("Device identifier is required for BLE connection.");
        }

        if (OperatingSystem.IsWindows())
        {
            if (!TryParseBluetoothAddress(connectionId, out var address))
            {
                throw new InvalidOperationException("Device address format is invalid.");
            }

            if (_windowsConnections.TryRemove(device.Id, out var existing))
            {
                try
                {
                    existing.Dispose();
                }
                catch
                {
                }
            }

            var attemptId = Interlocked.Increment(ref _connectSequence);
            try
            {
                BleLog.Info($"Connect attempt {attemptId} start (Windows). Address={device.MacAddress}; DeviceId={device.Id}; Name={device.Name}; IsConnected={device.IsConnected}; IsConnecting={device.IsConnecting}");
                var bleDevice = await WaitWithTimeout(
                    BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(),
                    ConnectTimeout,
                    "BluetoothLEDevice lookup");
                if (bleDevice is null)
                {
                    await LogBluetoothLookupFailureAsync(address, attemptId);
                    throw new InvalidOperationException("Bluetooth device not found.");
                }

                BleLog.Info($"BluetoothLEDevice resolved. Name={bleDevice.Name}; Status={bleDevice.ConnectionStatus}");
                LogWindowsDeviceDiagnostics(bleDevice, attemptId);

                var characteristic = await FindLedCharacteristicAsync(bleDevice);
                if (characteristic is null)
                {
                    bleDevice.Dispose();
                    BleLog.Error($"Connect attempt {attemptId} failed. LED characteristic not found.");
                    throw new InvalidOperationException("LED characteristic not found on device.");
                }

                TypedEventHandler<BluetoothLEDevice, object?>? handler = null;
                handler = (sender, _) =>
                {
                    if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
                    {
                        device.IsConnected = false;
                        if (_windowsConnections.TryRemove(device.Id, out var removed))
                        {
                            try
                            {
                                removed.Dispose();
                            }
                            catch
                            {
                            }
                        }
                    }
                };

                bleDevice.ConnectionStatusChanged += handler;
                _windowsConnections[device.Id] = new WindowsDeviceConnection(device, bleDevice, characteristic, handler);
                device.IsConnected = true;
                BleLog.Info($"Connect attempt {attemptId} success (Windows). Address={device.MacAddress}");
                return;
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                BleLog.Exception($"Connect attempt {attemptId} failed (Windows).", ex);
                throw new InvalidOperationException("Failed to connect to BLE device.", ex);
            }
        }

        try
        {
            var bluetoothDevice = await InTheHandBluetoothDevice.FromIdAsync(device.MacAddress);
            if (bluetoothDevice is null)
            {
                throw new InvalidOperationException("Bluetooth device not found.");
            }

            await bluetoothDevice.Gatt.ConnectAsync();

            var characteristic = await FindLedCharacteristicAsync(bluetoothDevice);
            if (characteristic is null)
            {
                bluetoothDevice.Gatt.Disconnect();
                throw new InvalidOperationException("LED characteristic not found on device.");
            }

            _connections[device.Id] = new DeviceConnection(device, bluetoothDevice, characteristic);
            device.IsConnected = true;
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            throw new InvalidOperationException("Failed to connect to BLE device.", ex);
        }
    }

    public Task DisconnectAsync(LedDevice device)
    {
        if (device is null)
        {
            return Task.CompletedTask;
        }

        if (OperatingSystem.IsWindows())
        {
            if (_windowsConnections.TryRemove(device.Id, out var windowsConnection))
            {
                try
                {
                    windowsConnection.Dispose();
                }
                catch
                {
                }
            }

            device.IsConnected = false;
            return Task.CompletedTask;
        }

        if (_connections.TryRemove(device.Id, out var classicConnection))
        {
            try
            {
                classicConnection.BluetoothDevice.Gatt.Disconnect();
            }
            catch
            {
            }
        }

        device.IsConnected = false;
        return Task.CompletedTask;
    }

    public async Task SendCommandAsync(LedDevice device, byte[] command)
    {
        if (device is null)
        {
            throw new ArgumentNullException(nameof(device));
        }

        if (command is null || command.Length == 0)
        {
            throw new ArgumentException("Command must contain at least one byte.", nameof(command));
        }

        if (OperatingSystem.IsWindows())
        {
            if (!_windowsConnections.TryGetValue(device.Id, out var windowsConnection))
            {
                throw new InvalidOperationException("Device is not connected.");
            }

            try
            {
                if (windowsConnection.BleDevice.ConnectionStatus != BluetoothConnectionStatus.Connected)
                {
                    device.IsConnected = false;
                    _windowsConnections.TryRemove(device.Id, out _);
                    throw new InvalidOperationException("Device is not connected.");
                }

                var writer = new DataWriter();
                writer.WriteBytes(command);
                var buffer = writer.DetachBuffer();
                await windowsConnection.Characteristic.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
            }
            catch (Exception ex)
            {
                device.IsConnected = false;
                _windowsConnections.TryRemove(device.Id, out _);
                BleLog.Exception("BLE write failed (Windows).", ex);
                throw new InvalidOperationException("BLE write failed; device disconnected.", ex);
            }

            return;
        }

        if (!_connections.TryGetValue(device.Id, out var classicConnection))
        {
            throw new InvalidOperationException("Device is not connected.");
        }

        try
        {
            await classicConnection.Characteristic.WriteValueWithoutResponseAsync(command);
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            _connections.TryRemove(device.Id, out _);
            throw new InvalidOperationException("BLE write failed; device disconnected.", ex);
        }
    }

    private static async Task<InTheHandGattCharacteristic?> FindLedCharacteristicAsync(InTheHandBluetoothDevice device)
    {
        var services = await device.Gatt.GetPrimaryServicesAsync();
        foreach (var service in services)
        {
            var characteristics = await service.GetCharacteristicsAsync();
            var match = characteristics.FirstOrDefault(c => c.Uuid == LedCharacteristicUuid);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private sealed record DeviceConnection(
        LedDevice Device,
        InTheHandBluetoothDevice BluetoothDevice,
        InTheHandGattCharacteristic Characteristic);

    private sealed class WindowsDeviceConnection : IDisposable
    {
        private readonly TypedEventHandler<BluetoothLEDevice, object?>? _statusHandler;

        public WindowsDeviceConnection(
            LedDevice device,
            BluetoothLEDevice bleDevice,
            WindowsGattCharacteristic characteristic,
            TypedEventHandler<BluetoothLEDevice, object?>? statusHandler)
        {
            Device = device;
            BleDevice = bleDevice;
            Characteristic = characteristic;
            _statusHandler = statusHandler;
        }

        public LedDevice Device { get; }
        public BluetoothLEDevice BleDevice { get; }
        public WindowsGattCharacteristic Characteristic { get; }

        public void Dispose()
        {
            if (_statusHandler is not null)
            {
                try
                {
                    BleDevice.ConnectionStatusChanged -= _statusHandler;
                }
                catch
                {
                }
            }

            try
            {
                BleDevice.Dispose();
            }
            catch
            {
            }
        }
    }

    private static async Task<WindowsGattCharacteristic?> FindLedCharacteristicAsync(Windows.Devices.Bluetooth.BluetoothLEDevice device)
    {
        var servicesResult = await WaitWithTimeout(
            device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(),
            ConnectTimeout,
            "GetGattServices");
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            BleLog.Error($"GetGattServices failed. Status={servicesResult.Status}; ProtocolError={FormatProtocolError(servicesResult.ProtocolError)}; ConnectionStatus={device.ConnectionStatus}");
            return null;
        }

        BleLog.Info($"Gatt services discovered: {servicesResult.Services.Count}; ConnectionStatus={device.ConnectionStatus}");
        foreach (var service in servicesResult.Services)
        {
            BleLog.Info($"Gatt service found: {service.Uuid}");
            var characteristicsResult = await WaitWithTimeout(
                service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(),
                ConnectTimeout,
                "GetCharacteristics");
            if (characteristicsResult.Status != GattCommunicationStatus.Success)
            {
                BleLog.Error($"GetCharacteristics failed. Status={characteristicsResult.Status}; ProtocolError={FormatProtocolError(characteristicsResult.ProtocolError)}; Service={service.Uuid}");
                continue;
            }

            BleLog.Info($"Gatt characteristics discovered: {characteristicsResult.Characteristics.Count} for service {service.Uuid}");
            var match = characteristicsResult.Characteristics.FirstOrDefault(c => c.Uuid == LedCharacteristicUuid);
            if (match is not null)
            {
                BleLog.Info($"Found LED characteristic {LedCharacteristicUuid}");
                return match;
            }
        }

        BleLog.Error($"LED characteristic {LedCharacteristicUuid} not found after scanning {servicesResult.Services.Count} services.");
        return null;
    }

    private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string context)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException($"{context} timed out after {timeout.TotalSeconds:0} seconds.");
        }

        return await task;
    }

    private static string FormatBluetoothAddress(ulong address)
    {
        var hex = address.ToString("X12", CultureInfo.InvariantCulture);
        return string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool TryParseBluetoothAddress(string value, out ulong address)
    {
        var hex = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();

        return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }

    private static string FormatProtocolError(byte? protocolError)
    {
        return protocolError.HasValue ? $"0x{protocolError.Value:X2}" : "n/a";
    }

    private static void LogWindowsDeviceDiagnostics(BluetoothLEDevice device, int attemptId)
    {
        try
        {
            var info = device.DeviceInformation;
            var pairing = info.Pairing;
            var access = DeviceAccessInformation.CreateFromId(device.DeviceId);
            BleLog.Info(
                $"Connect attempt {attemptId} device info: Id={device.DeviceId}; Name={device.Name}; " +
                $"BluetoothAddress={FormatBluetoothAddress(device.BluetoothAddress)}; Status={device.ConnectionStatus}; " +
                $"Paired={pairing.IsPaired}; CanPair={pairing.CanPair}; Protection={pairing.ProtectionLevel}; Access={access.CurrentStatus}");
        }
        catch (Exception ex)
        {
            BleLog.Exception($"Connect attempt {attemptId} device diagnostics failed.", ex);
        }
    }

    private static async Task LogBluetoothLookupFailureAsync(ulong address, int attemptId)
    {
        try
        {
            var selector = BluetoothLEDevice.GetDeviceSelectorFromBluetoothAddress(address);
            var devices = await DeviceInformation.FindAllAsync(selector);
            BleLog.Info($"Connect attempt {attemptId} lookup diagnostics: DeviceInformation count={devices.Count}");
            foreach (var info in devices.Take(5))
            {
                BleLog.Info(
                    $"Connect attempt {attemptId} device info candidate: Id={info.Id}; Name={info.Name}; " +
                    $"Paired={info.Pairing.IsPaired}; CanPair={info.Pairing.CanPair}; Kind={info.Kind}");
            }
        }
        catch (Exception ex)
        {
            BleLog.Exception($"Connect attempt {attemptId} lookup diagnostics failed.", ex);
        }
    }
}
