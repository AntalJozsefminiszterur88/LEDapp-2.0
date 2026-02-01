using System.Collections.Concurrent;
using LedController.Core.Interfaces;
using LedController.Core.Models;
using InTheHand.Bluetooth;

namespace LedController.Infrastructure.Services;

public sealed class BleService : IBleService
{
    private static readonly Guid LedCharacteristicUuid = Guid.Parse("0000fff3-0000-1000-8000-00805f9b34fb");

    private readonly ConcurrentDictionary<Guid, DeviceConnection> _connections = new();

    public async Task<IReadOnlyList<LedDevice>> ScanForDevicesAsync()
    {
        try
        {
            var devices = await Bluetooth.ScanForDevicesAsync();
            return devices
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
        }
        catch
        {
            return Array.Empty<LedDevice>();
        }
    }

    public async Task ConnectAsync(LedDevice device)
    {
        if (device is null)
        {
            throw new ArgumentNullException(nameof(device));
        }

        if (string.IsNullOrWhiteSpace(device.MacAddress))
        {
            throw new InvalidOperationException("Device MacAddress is required for BLE connection.");
        }

        try
        {
            var bluetoothDevice = await BluetoothDevice.FromIdAsync(device.MacAddress);
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

        if (_connections.TryRemove(device.Id, out var connection))
        {
            try
            {
                connection.BluetoothDevice.Gatt.Disconnect();
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

        if (!_connections.TryGetValue(device.Id, out var connection))
        {
            throw new InvalidOperationException("Device is not connected.");
        }

        try
        {
            await connection.Characteristic.WriteValueWithoutResponseAsync(command);
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            _connections.TryRemove(device.Id, out _);
            throw new InvalidOperationException("BLE write failed; device disconnected.", ex);
        }
    }

    private static async Task<GattCharacteristic?> FindLedCharacteristicAsync(BluetoothDevice device)
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
        BluetoothDevice BluetoothDevice,
        GattCharacteristic Characteristic);
}
