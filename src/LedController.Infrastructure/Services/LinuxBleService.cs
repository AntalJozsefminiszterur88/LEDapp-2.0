using System.Collections.Concurrent;
using System.Threading;
using LedController.Core.Interfaces;
using LedController.Core.Models;
using Linux.Bluetooth;
using Linux.Bluetooth.Extensions;
using Tmds.DBus;

using LinuxBleDevice = Linux.Bluetooth.Device;
using LinuxGattCharacteristic = Linux.Bluetooth.IGattCharacteristic1;

namespace LedController.Infrastructure.Services;

public sealed class LinuxBleService : IBleService
{
    private static readonly TimeSpan ScanDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResolveScanDuration = TimeSpan.FromSeconds(6);
    private static readonly string LedCharacteristicUuid = "0000fff3-0000-1000-8000-00805f9b34fb";
    private static int _connectSequence;

    private readonly ConcurrentDictionary<Guid, DeviceConnection> _connections = new();
    private readonly PeriodicTimer _monitorTimer = new(TimeSpan.FromSeconds(5));

    public LinuxBleService()
    {
        _ = MonitorConnectionsAsync();
    }

    private async Task MonitorConnectionsAsync()
    {
        while (await _monitorTimer.WaitForNextTickAsync())
        {
            foreach (var kvp in _connections)
            {
                var connection = kvp.Value;
                try
                {
                    var isConnected = await connection.BluetoothDevice.GetConnectedAsync();
                    if (!isConnected)
                    {
                        if (connection.Device.IsConnected)
                        {
                            connection.Device.IsConnected = false;
                            BleLog.Info($"[Monitor] Detected native disconnection for {connection.Device.MacAddress}");
                        }
                        _connections.TryRemove(kvp.Key, out _);
                    }
                }
                catch
                {
                    if (connection.Device.IsConnected)
                    {
                        connection.Device.IsConnected = false;
                        BleLog.Info($"[Monitor] Error checking connection for {connection.Device.MacAddress}, assuming disconnected.");
                    }
                    _connections.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public async Task<IReadOnlyList<LedDevice>> ScanForDevicesAsync()
    {
        BleLog.Info($"Scan start (Linux). OS={Environment.OSVersion}; Framework={Environment.Version}; Is64Bit={Environment.Is64BitProcess}.");

        var adapter = await GetAdapterAsync();
        try
        {
            await adapter.StartDiscoveryAsync();
            await Task.Delay(ScanDuration);
        }
        finally
        {
            try
            {
                await adapter.StopDiscoveryAsync();
            }
            catch
            {
            }
        }

        var devices = await adapter.GetDevicesAsync();
        var mappedDevices = await Task.WhenAll(
            devices
                .Where(device => device is not null)
                .Select(MapDeviceAsync));

        var results = mappedDevices
            .Where(device => !string.IsNullOrWhiteSpace(device.DeviceIdentifier))
            .GroupBy(device => device.DeviceIdentifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.Name)
            .ToList();

        BleLog.Info($"Scan complete (Linux). Found {results.Count} devices.");
        return results;
    }

    public async Task ConnectAsync(LedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var stableId = LedDeviceIdentity.GetStableId(device);
        if (string.IsNullOrWhiteSpace(stableId))
        {
            throw new InvalidOperationException("Device identifier is required for BLE connection.");
        }

        var adapter = await GetAdapterAsync();
        var bluetoothDevice = await ResolveDeviceAsync(adapter, stableId);
        if (bluetoothDevice is null)
        {
            BleLog.Error($"Connect failed (Linux). Device not found. Address={device.MacAddress}; DeviceIdentifier={stableId}; Name={device.Name}");
            throw new InvalidOperationException("Bluetooth device not found.");
        }

        var attemptId = Interlocked.Increment(ref _connectSequence);
        device.IsConnecting = true;
        try
        {
            BleLog.Info(
                $"Connect attempt {attemptId} start (Linux). Address={device.MacAddress}; DeviceIdentifier={stableId}; Name={device.Name}; " +
                $"IsConnected={device.IsConnected}; IsConnecting={device.IsConnecting}");

            try
            {
                await WaitWithTimeout(bluetoothDevice.ConnectAsync(), ConnectTimeout, "ConnectAsync");
            }
            catch (DBusException dbusEx) when (dbusEx.ErrorName == "org.bluez.Error.Failed" && dbusEx.Message.Contains("Operation already in progress"))
            {
                BleLog.Info($"Connect attempt {attemptId} failed: Operation already in progress. Attempting to force disconnect and retry.");
                try
                {
                    await bluetoothDevice.DisconnectAsync();
                    await Task.Delay(1000);
                    await WaitWithTimeout(bluetoothDevice.ConnectAsync(), ConnectTimeout, "ConnectAsync Retry");
                }
                catch (Exception retryEx)
                {
                    BleLog.Exception($"Connect attempt {attemptId} retry failed.", retryEx);
                    throw;
                }
            }

            await bluetoothDevice.WaitForPropertyValueAsync("Connected", value: true, ConnectTimeout);
            await bluetoothDevice.WaitForPropertyValueAsync("ServicesResolved", value: true, ConnectTimeout);

            BleLog.Info($"Connect attempt {attemptId} connected (Linux). Address={stableId}");

            var characteristic = await FindLedCharacteristicAsync(bluetoothDevice, attemptId);

            if (characteristic is null)
            {
                await bluetoothDevice.DisconnectAsync();
                BleLog.Error($"Connect attempt {attemptId} failed (Linux). LED characteristic not found.");
                throw new InvalidOperationException("LED characteristic not found on device.");
            }

            device.DeviceIdentifier = stableId;
            if (string.IsNullOrWhiteSpace(device.MacAddress))
            {
                device.MacAddress = stableId;
            }

            _connections[device.Id] = new DeviceConnection(device, bluetoothDevice, characteristic);
            device.IsConnected = true;
            BleLog.Info($"Connect attempt {attemptId} success (Linux). Address={stableId}");
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            BleLog.Exception($"Connect attempt {attemptId} failed (Linux).", ex);
            throw new InvalidOperationException("Failed to connect to BLE device.", ex);
        }
        finally
        {
            device.IsConnecting = false;
        }
    }

    public async Task DisconnectAsync(LedDevice device)
    {
        if (device is null)
        {
            return;
        }

        if (_connections.TryRemove(device.Id, out var connection))
        {
            try
            {
                await connection.BluetoothDevice.DisconnectAsync();
                BleLog.Info($"Disconnect success (Linux). Address={device.MacAddress}; DeviceIdentifier={device.DeviceIdentifier}; Name={device.Name}");
            }
            catch
            {
            }
        }

        device.IsConnected = false;
    }

    public async Task SendCommandAsync(LedDevice device, byte[] command)
    {
        ArgumentNullException.ThrowIfNull(device);

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
            await WaitWithTimeout(connection.Characteristic.WriteValueAsync(
                command,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "command"
                }), TimeSpan.FromSeconds(5), "WriteValueAsync");
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            _connections.TryRemove(device.Id, out _);
            BleLog.Exception($"BLE write failed (Linux). Address={device.MacAddress}; DeviceIdentifier={device.DeviceIdentifier}; Name={device.Name}", ex);
            throw new InvalidOperationException("BLE write failed; device disconnected.", ex);
        }
    }

    private static async Task<IAdapter1> GetAdapterAsync()
    {
        var adapter = (await BlueZManager.GetAdaptersAsync()).FirstOrDefault();
        if (adapter is null)
        {
            throw new InvalidOperationException("Bluetooth adapter not found.");
        }

        return adapter;
    }

    private static async Task<LinuxBleDevice?> ResolveDeviceAsync(IAdapter1 adapter, string stableId)
    {
        var device = await adapter.GetDeviceAsync(stableId);
        if (device is not null)
        {
            return device;
        }

        try
        {
            await adapter.StartDiscoveryAsync();
            await Task.Delay(ResolveScanDuration);
        }
        finally
        {
            try
            {
                await adapter.StopDiscoveryAsync();
            }
            catch
            {
            }
        }

        var devices = await adapter.GetDevicesAsync();
        foreach (var candidate in devices)
        {
            if (candidate is null)
            {
                continue;
            }

            var address = await candidate.GetAddressAsync();
            if (string.Equals(address, stableId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<LedDevice> MapDeviceAsync(LinuxBleDevice device)
    {
        var address = await device.GetAddressAsync() ?? string.Empty;
        var name = await device.GetAliasAsync() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = address;
        }

        return new LedDevice
        {
            Name = name,
            MacAddress = address,
            DeviceIdentifier = address
        };
    }

    private static async Task<LinuxGattCharacteristic?> FindLedCharacteristicAsync(LinuxBleDevice device, int attemptId)
    {
        var services = await WaitWithTimeout(device.GetServicesAsync(), ConnectTimeout, "GetServicesAsync");
        BleLog.Info($"Connect attempt {attemptId} services discovered (Linux): {services.Count}");

        foreach (var service in services)
        {
            var serviceUuid = await service.GetUUIDAsync();
            BleLog.Info($"Connect attempt {attemptId} service found (Linux): {serviceUuid}");

            var characteristics = await WaitWithTimeout(
                service.GetCharacteristicsAsync(),
                ConnectTimeout,
                $"GetCharacteristicsAsync {serviceUuid}");

            BleLog.Info($"Connect attempt {attemptId} characteristics discovered (Linux): {characteristics.Count} for service {serviceUuid}");
            foreach (var characteristic in characteristics)
            {
                var characteristicUuid = await characteristic.GetUUIDAsync();
                if (string.Equals(characteristicUuid, LedCharacteristicUuid, StringComparison.OrdinalIgnoreCase))
                {
                    BleLog.Info($"Connect attempt {attemptId} found LED characteristic (Linux): {characteristicUuid}");
                    return characteristic;
                }
            }
        }

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

    private static async Task WaitWithTimeout(Task task, TimeSpan timeout, string context)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            throw new TimeoutException($"{context} timed out after {timeout.TotalSeconds:0} seconds.");
        }

        await task;
    }

    private sealed record DeviceConnection(
        LedDevice Device,
        LinuxBleDevice BluetoothDevice,
        LinuxGattCharacteristic Characteristic);
}
