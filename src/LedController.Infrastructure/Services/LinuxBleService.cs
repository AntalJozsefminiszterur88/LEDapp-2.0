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
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HeartbeatIdleThreshold = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AdapterPowerToggleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AdapterRecoverySettleDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AdapterRecoveryScanDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AdapterRecoveryCooldown = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BaseConnectBackoffDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxConnectBackoffDelay = TimeSpan.FromMinutes(5);
    private static readonly string LedCharacteristicUuid = "0000fff3-0000-1000-8000-00805f9b34fb";
    private static readonly byte[] KeepAliveCommand = Convert.FromHexString("7e00000000000000ef");
    private const int AdapterRecoveryFailureThreshold = 2;
    private static int _connectSequence;
    private static DateTime _lastAdapterRecoveryUtc = DateTime.MinValue;

    private readonly ConcurrentDictionary<Guid, DeviceConnection> _connections = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _connectGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _recoverableFailureCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectBackoffState> _connectBackoffs = new(StringComparer.OrdinalIgnoreCase);
    private readonly PeriodicTimer _monitorTimer = new(TimeSpan.FromSeconds(5));
    private readonly PeriodicTimer _heartbeatTimer = new(HeartbeatInterval);
    private readonly SemaphoreSlim _adapterRecoveryGate = new(1, 1);

    public LinuxBleService()
    {
        _ = MonitorConnectionsAsync();
        _ = HeartbeatAsync();
    }


    private async Task HeartbeatAsync()
    {
        while (await _heartbeatTimer.WaitForNextTickAsync())
        {
            foreach (var kvp in _connections)
            {
                var connection = kvp.Value;
                try
                {
                    if (!connection.Device.IsConnected)
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - connection.LastActivityUtc < HeartbeatIdleThreshold)
                    {
                        continue;
                    }

                    BleLog.Info($"[Heartbeat] Sending KeepAlive for {connection.Device.PrimaryName}"); await WriteCommandAsync(connection, KeepAliveCommand, "Heartbeat KeepAlive", skipIfBusy: true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException)
                {
                    MarkConnectionLost(kvp.Key, connection, "Heartbeat timed out.");
                }
                catch
                {
                    MarkConnectionLost(kvp.Key, connection, "Heartbeat write failed.");
                }
            }
        }
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
                        MarkConnectionLost(kvp.Key, connection, $"[Monitor] Detected native disconnection for {connection.Device.MacAddress}");
                    }
                }
                catch
                {
                    MarkConnectionLost(kvp.Key, connection, $"[Monitor] Error checking connection for {connection.Device.MacAddress}, assuming disconnected.");
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

    public async Task<bool> IsDeviceConnectedAsync(LedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!TryGetExistingConnection(device, out var connectionKey, out var existingConnection))
        {
            device.IsConnected = false;
            return false;
        }

        try
        {
            if (!await existingConnection.BluetoothDevice.GetConnectedAsync())
            {
                _connections.TryRemove(connectionKey, out _);
                device.IsConnected = false;
                return false;
            }
        }
        catch
        {
            _connections.TryRemove(connectionKey, out _);
            device.IsConnected = false;
            return false;
        }

        PromoteConnection(device, connectionKey, existingConnection);
        device.IsConnected = true;
        device.IsConnecting = false;
        return true;
    }

    public async Task ConnectAsync(LedDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var stableId = LedDeviceIdentity.GetStableId(device);
        if (string.IsNullOrWhiteSpace(stableId))
        {
            throw new InvalidOperationException("Device identifier is required for BLE connection.");
        }

        var connectGate = _connectGates.GetOrAdd(stableId, _ => new SemaphoreSlim(1, 1));
        await connectGate.WaitAsync();
        try
        {
            if (await TryReuseExistingConnectionAsync(device))
            {
                return;
            }

            ThrowIfConnectBackoffActive(stableId);

            var adapter = await GetAdapterAsync();
            LinuxBleDevice? bluetoothDevice = null;
            bluetoothDevice = await ResolveDeviceAsync(adapter, stableId);
            if (bluetoothDevice is null)
            {
                BleLog.Error($"Connect failed (Linux). Device not found. Address={device.MacAddress}; DeviceIdentifier={stableId}; Name={device.Name}");
                throw new InvalidOperationException("Bluetooth device not found.");
            }

            var attemptId = Interlocked.Increment(ref _connectSequence);
            device.IsConnecting = true;

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
            _recoverableFailureCounts.TryRemove(stableId, out _);
            _connectBackoffs.TryRemove(stableId, out _);
            BleLog.Info($"Connect attempt {attemptId} success (Linux). Address={stableId}");
        }
        catch (BleConnectBackoffException)
        {
            device.IsConnected = false;
            throw;
        }
        catch (Exception ex)
        {
            device.IsConnected = false;
            ApplyConnectBackoff(stableId, ex);
            try
            {
                var adapter = await GetAdapterAsync();
                await StopDiscoveryIfNeededAsync(adapter);
                var removed = await TryRemoveDeviceStateAsync(adapter, stableId);
                if (removed)
                {
                    BleLog.Info($"Connect cleanup removed stale BlueZ device state for {stableId}.");
                }

                await TryRecoverAdapterAsync(adapter, stableId, ex);
            }
            catch
            {
                // Cleanup and recovery are best-effort only.
            }

            BleLog.Exception($"Connect failed (Linux). Address={device.MacAddress}; DeviceIdentifier={stableId}; Name={device.Name}", ex);
            throw new InvalidOperationException("Failed to connect to BLE device.", ex);
        }
        finally
        {
            device.IsConnecting = false;
            connectGate.Release();
        }
    }

    private void ThrowIfConnectBackoffActive(string stableId)
    {
        if (!_connectBackoffs.TryGetValue(stableId, out var state))
        {
            return;
        }

        DateTime nextAttemptUtc;
        int failureCount;
        lock (state.SyncRoot)
        {
            nextAttemptUtc = state.NextAttemptUtc;
            failureCount = state.FailureCount;
        }

        var nowUtc = DateTime.UtcNow;
        if (nowUtc >= nextAttemptUtc)
        {
            return;
        }

        var remaining = nextAttemptUtc - nowUtc;
        throw new BleConnectBackoffException(
            $"BLE connection for {stableId} is in backoff for {remaining.TotalSeconds:0}s after {failureCount} failed attempts.");
    }

    private void ApplyConnectBackoff(string stableId, Exception failure)
    {
        var state = _connectBackoffs.GetOrAdd(stableId, _ => new ConnectBackoffState());
        int failureCount;
        DateTime nextAttemptUtc;
        double delaySeconds;

        lock (state.SyncRoot)
        {
            state.FailureCount++;
            failureCount = state.FailureCount;

            var multiplier = Math.Pow(2, Math.Min(failureCount - 1, 5));
            delaySeconds = Math.Min(BaseConnectBackoffDelay.TotalSeconds * multiplier, MaxConnectBackoffDelay.TotalSeconds);
            nextAttemptUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
            state.NextAttemptUtc = nextAttemptUtc;
        }

        BleLog.Info(
            $"Connect backoff scheduled for {stableId}. FailureCount={failureCount}; " +
            $"NextAttemptUtc={nextAttemptUtc:O}; DelaySeconds={delaySeconds:0}; Cause={failure.GetType().Name}: {failure.Message}");
    }

    public async Task DisconnectAsync(LedDevice device)
    {
        if (device is null)
        {
            return;
        }

        if (TryGetExistingConnection(device, out var connectionKey, out var connection))
        {
            _connections.TryRemove(connectionKey, out _);
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

        if (!TryGetExistingConnection(device, out var connectionKey, out var connection))
        {
            throw new InvalidOperationException("Device is not connected.");
        }

        try
        {
            await WriteCommandAsync(connection, command, "WriteValueAsync");
        }
        catch (Exception ex)
        {
            MarkConnectionLost(connectionKey, connection, $"BLE write failed (Linux). Address={device.MacAddress}; DeviceIdentifier={device.DeviceIdentifier}; Name={device.Name}");
            BleLog.Exception($"BLE write failed (Linux). Address={device.MacAddress}; DeviceIdentifier={device.DeviceIdentifier}; Name={device.Name}", ex);
            throw new InvalidOperationException("BLE write failed; device disconnected.", ex);
        }
    }

    private async Task<bool> TryReuseExistingConnectionAsync(LedDevice device)
    {
        if (!TryGetExistingConnection(device, out var connectionKey, out var existingConnection))
        {
            return false;
        }

        try
        {
            if (!await existingConnection.BluetoothDevice.GetConnectedAsync())
            {
                _connections.TryRemove(connectionKey, out _);
                return false;
            }
        }
        catch
        {
            _connections.TryRemove(connectionKey, out _);
            return false;
        }

        PromoteConnection(device, connectionKey, existingConnection);
        device.IsConnected = true;
        device.IsConnecting = false;

        BleLog.Info($"Reused existing BLE connection (Linux). Address={device.MacAddress}; DeviceIdentifier={device.DeviceIdentifier}; Name={device.Name}");
        return true;
    }

    private bool TryGetExistingConnection(LedDevice device, out Guid connectionKey, out DeviceConnection existingConnection)
    {
        if (_connections.TryGetValue(device.Id, out existingConnection!))
        {
            connectionKey = device.Id;
            return true;
        }

        foreach (var kvp in _connections)
        {
            if (!LedDeviceIdentity.Matches(kvp.Value.Device, device))
            {
                continue;
            }

            connectionKey = kvp.Key;
            existingConnection = kvp.Value;
            return true;
        }

        connectionKey = Guid.Empty;
        existingConnection = null!;
        return false;
    }

    private void PromoteConnection(LedDevice device, Guid connectionKey, DeviceConnection existingConnection)
    {
        var promotedConnection = existingConnection;
        promotedConnection.Device = device;

        if (connectionKey != device.Id)
        {
            _connections.TryRemove(connectionKey, out _);
        }

        _connections[device.Id] = promotedConnection;
        device.DeviceIdentifier = string.IsNullOrWhiteSpace(device.DeviceIdentifier)
            ? promotedConnection.Device.DeviceIdentifier
            : device.DeviceIdentifier;
        device.MacAddress = string.IsNullOrWhiteSpace(device.MacAddress)
            ? promotedConnection.Device.MacAddress
            : device.MacAddress;
    }

    private async Task<bool> WriteCommandAsync(
        DeviceConnection connection,
        byte[] command,
        string context,
        bool skipIfBusy = false)
    {
        if (skipIfBusy)
        {
            if (!connection.TryBeginCommand())
            {
                return false;
            }
        }
        else
        {
            await connection.BeginCommandAsync();
        }

        try
        {
            await WaitWithTimeout(connection.Characteristic.WriteValueAsync(
                command,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["type"] = "command"
                }), CommandTimeout, context);

            connection.RememberCommand(command);
            connection.Touch();
            return true;
        }
        finally
        {
            connection.EndCommand();
        }
    }

    private void MarkConnectionLost(Guid connectionKey, DeviceConnection connection, string reason)
    {
        _connections.TryRemove(connectionKey, out _);
        if (!connection.Device.IsConnected)
        {
            return;
        }

        connection.Device.IsConnected = false;
        BleLog.Info(reason);
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

    private async Task TryRecoverAdapterAsync(IAdapter1 adapter, string stableId, Exception failure)
    {
        if (!IsRecoverableConnectFailure(failure))
        {
            _recoverableFailureCounts.TryRemove(stableId, out _);
            return;
        }

        var failureCount = _recoverableFailureCounts.AddOrUpdate(stableId, 1, static (_, current) => current + 1);
        BleLog.Info(
            $"Connect recovery candidate for {stableId}. RecoverableFailureCount={failureCount}; Threshold={AdapterRecoveryFailureThreshold}");

        if (failureCount < AdapterRecoveryFailureThreshold)
        {
            return;
        }

        await _adapterRecoveryGate.WaitAsync();
        try
        {
            var nowUtc = DateTime.UtcNow;
            var remainingCooldown = (_lastAdapterRecoveryUtc + AdapterRecoveryCooldown) - nowUtc;
            if (remainingCooldown > TimeSpan.Zero)
            {
                BleLog.Info(
                    $"Adapter recovery skipped for {stableId} because cooldown is active. RemainingSeconds={remainingCooldown.TotalSeconds:0}");
                return;
            }

            BleLog.Info($"Adapter recovery start for {stableId}. Power-cycling hci adapter.");
            await StopDiscoveryIfNeededAsync(adapter);
            await TryRemoveDeviceStateAsync(adapter, stableId);

            await adapter.SetPoweredAsync(false);
            await WaitForAdapterPoweredStateAsync(adapter, powered: false, AdapterPowerToggleTimeout);
            await Task.Delay(AdapterRecoverySettleDelay);

            await adapter.SetPoweredAsync(true);
            await WaitForAdapterPoweredStateAsync(adapter, powered: true, AdapterPowerToggleTimeout);
            await Task.Delay(AdapterRecoverySettleDelay);

            await WarmAdapterAsync(adapter);

            _lastAdapterRecoveryUtc = DateTime.UtcNow;
            _recoverableFailureCounts.TryRemove(stableId, out _);
            BleLog.Info($"Adapter recovery success for {stableId}.");
        }
        catch (Exception recoveryEx)
        {
            BleLog.Exception($"Adapter recovery failed for {stableId}.", recoveryEx);
        }
        finally
        {
            _adapterRecoveryGate.Release();
        }
    }

    private static bool IsRecoverableConnectFailure(Exception ex)
    {
        if (ex is TimeoutException timeoutEx &&
            timeoutEx.Message.Contains("ServicesResolved", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ex is DBusException dbusEx &&
            string.Equals(dbusEx.ErrorName, "org.bluez.Error.Failed", StringComparison.Ordinal) &&
            (dbusEx.Message.Contains("le-connection-abort-by-local", StringComparison.OrdinalIgnoreCase) ||
             dbusEx.Message.Contains("Operation already in progress", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static async Task StopDiscoveryIfNeededAsync(IAdapter1 adapter)
    {
        try
        {
            if (!await adapter.GetDiscoveringAsync())
            {
                return;
            }
        }
        catch
        {
            return;
        }

        try
        {
            await adapter.StopDiscoveryAsync();
        }
        catch
        {
        }
    }

    private static async Task<bool> TryRemoveDeviceStateAsync(IAdapter1 adapter, string stableId)
    {
        var bluetoothDevice = await adapter.GetDeviceAsync(stableId);
        if (bluetoothDevice is null)
        {
            return false;
        }

        try
        {
            await bluetoothDevice.DisconnectAsync();
        }
        catch
        {
        }

        try
        {
            await adapter.RemoveDeviceAsync(bluetoothDevice.ObjectPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WarmAdapterAsync(IAdapter1 adapter)
    {
        try
        {
            await adapter.StartDiscoveryAsync();
            await Task.Delay(AdapterRecoveryScanDuration);
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
    }

    private static async Task WaitForAdapterPoweredStateAsync(IAdapter1 adapter, bool powered, TimeSpan timeout)
    {
        var deadlineUtc = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (await adapter.GetPoweredAsync() == powered)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for adapter Powered={powered}.");
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

    private sealed class DeviceConnection
    {
        public DeviceConnection(
            LedDevice device,
            LinuxBleDevice bluetoothDevice,
            LinuxGattCharacteristic characteristic)
        {
            Device = device;
            BluetoothDevice = bluetoothDevice;
            Characteristic = characteristic;
            Touch();
        }

        public LedDevice Device { get; set; }

        public LinuxBleDevice BluetoothDevice { get; }

        public LinuxGattCharacteristic Characteristic { get; }

        public byte[]? LastCommand { get; private set; }

        public DateTime LastActivityUtc { get; private set; }

        private SemaphoreSlim CommandGate { get; } = new(1, 1);

        public void RememberCommand(byte[] command)
        {
            LastCommand = command.ToArray();
        }

        public void Touch()
        {
            LastActivityUtc = DateTime.UtcNow;
        }

        public Task BeginCommandAsync()
        {
            return CommandGate.WaitAsync();
        }

        public bool TryBeginCommand()
        {
            return CommandGate.Wait(0);
        }

        public void EndCommand()
        {
            CommandGate.Release();
        }
    }

    private sealed class ConnectBackoffState
    {
        public object SyncRoot { get; } = new();

        public int FailureCount { get; set; }

        public DateTime NextAttemptUtc { get; set; } = DateTime.MinValue;
    }

    private sealed class BleConnectBackoffException : InvalidOperationException
    {
        public BleConnectBackoffException(string message)
            : base(message)
        {
        }
    }
}
