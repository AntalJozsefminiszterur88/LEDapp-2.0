# core/ble_controller.py (Logolással + eredeti szűréssel)

import asyncio
from bleak import BleakClient, BleakScanner, BleakError
from config import CHARACTERISTIC_UUID
import traceback  # Hozzáadás a tracebackhez

# Logolás importálása
try:
    from core.reconnect_handler import log_event
except ImportError:
    try:
        from .reconnect_handler import log_event
    except ImportError:

        def log_event(msg):
            print(f"[LOG - Dummy BLEController]: {msg}")


class BLEController:
    BLUETOOTH_OFF_WINERRORS = {-2147020577}
    BLUETOOTH_OFF_STRINGS = [
        "bluetooth adapter is off",
        "bluetooth is turned off",
        "org.bluez.error.notready",
        "das gerät kann nicht verwendet werden",
        "device not ready",
    ]

    def __init__(self):
        self.client = None
        self._connection_lock = asyncio.Lock()

    def _is_bluetooth_off_error(self, exc: Exception) -> bool:
        """Heurisztikusan megállapítja, hogy a kivétel a Bluetooth kikapcsolt
        állapotára utal-e."""
        msg = str(exc).lower()
        if isinstance(exc, OSError):
            winerr = getattr(exc, "winerror", None)
            if winerr in self.BLUETOOTH_OFF_WINERRORS:
                return True
        for s in self.BLUETOOTH_OFF_STRINGS:
            if s in msg:
                return True
        return False

    async def scan(self):
        """Eszközök keresése (bővített logolással)."""
        log_event("BLEController: Starting BleakScanner.discover...")  # <<< ÚJ LOG >>>
        devices_list = []
        try:
            # Növelt timeout
            discovered = await BleakScanner.discover(timeout=12.0)
            log_event(f"BLEController: Discover finished. Found {len(discovered)} raw devices.")  # <<< ÚJ LOG >>>
            if discovered:
                log_event("BLEController: Processing discovered devices...")  # <<< ÚJ LOG >>>
                for d in discovered:
                    name = d.name if d.name else "Unnamed Device"
                    address = d.address
                    log_event(f"  - Processing: {name} ({address})")  # <<< ÚJ LOG >>>
                    # Eredeti szűrés visszaállítása: Csak névvel rendelkező eszközök
                    if d.name:
                        devices_list.append((name, address))
                    else:
                        log_event(f"  - Skipping unnamed device: {address}")  # <<< ÚJ LOG >>>
            else:
                log_event("BLEController: No devices discovered by BleakScanner.")  # <<< ÚJ LOG >>>

        except Exception as e:
            log_event(f"BLEController: Error during scan execution: {e}")  # <<< ÚJ LOG >>>
            log_event(f"Traceback:\n{traceback.format_exc()}")  # <<< ÚJ LOG >>>
            if self._is_bluetooth_off_error(e):
                raise RuntimeError(
                    "A Bluetooth ki van kapcsolva vagy nem érhető el. Kapcsolja be, majd próbálja újra."
                ) from e
            devices_list = []  # Hiba esetén üres lista

        log_event(f"BLEController: Returning {len(devices_list)} named devices to AsyncHelper.")  # <<< ÚJ LOG >>>
        return devices_list  # Csak a névvel rendelkezőket adjuk vissza

    # connect, disconnect, send_command metódusok változatlanok maradnak
    async def connect(self, address):
        """Csatlakozás az eszközhöz címmel (egyszeri próbálkozás)."""
        async with self._connection_lock:
            if self.client and self.client.is_connected:
                if self.client.address.upper() == address.upper():
                    log_event(f"BLEController: Már csatlakozva: {address}")
                    return True
                else:
                    await self.disconnect()

            log_event(f"BLEController: Csatlakozás kísérlet: {address}")
            if self.client:
                pass  # Disconnect már megtörtént

            self.client = BleakClient(address)
            try:
                await self.client.connect(timeout=15.0)
                log_event(f"BLEController: Sikeres csatlakozás: {address}")
                return True
            except Exception as e:
                log_event(f"BLEController: Csatlakozási hiba a connect() során ({type(e).__name__}): {e}")
                self.client = None
                raise e

    async def connect_with_retry(self, address, attempts=3, delay=1.0, timeout=15.0):
        """Try connecting multiple times before giving up."""
        last_exc = None
        for attempt in range(1, attempts + 1):
            try:
                log_event(f"BLEController: Connection attempt {attempt}/{attempts} to {address}")
                # reuse the single attempt connect with specified timeout
                async with self._connection_lock:
                    if self.client and self.client.is_connected:
                        if self.client.address.upper() == address.upper():
                            log_event(f"BLEController: Already connected: {address}")
                            return True
                        else:
                            await self.disconnect()

                    self.client = BleakClient(address)
                    await self.client.connect(timeout=timeout)
                    log_event(f"BLEController: Connected on attempt {attempt}: {address}")
                    return True
            except Exception as e:
                last_exc = e
                log_event(f"BLEController: Connect error on attempt {attempt} ({type(e).__name__}): {e}")
                self.client = None
                if attempt < attempts:
                    await asyncio.sleep(delay)
        if last_exc:
            raise last_exc
        raise BleakError("Unknown connection failure")

    async def disconnect(self):
        """Kapcsolat bontása."""
        async with self._connection_lock:
            client_to_disconnect = self.client
            self.client = None
            if client_to_disconnect and client_to_disconnect.is_connected:
                log_event(f"BLEController: disconnect() hívása: {client_to_disconnect.address}")
                try:
                    await client_to_disconnect.disconnect()
                    log_event(f"BLEController: disconnect() sikeres.")
                except BleakError as e:
                    log_event(f"BLEController: Hiba a kapcsolat bontása közben: {e}")

    async def send_command(self, hex_command):
        """Parancs küldése a csatlakoztatott eszköznek."""
        if self.client and self.client.is_connected:
            try:
                await self.client.write_gatt_char(CHARACTERISTIC_UUID, bytes.fromhex(hex_command), response=False)
            except BleakError as e:
                log_event(f"BLEController: Hiba parancs küldésekor ({hex_command}): {e}")
                raise e
            except Exception as e:
                log_event(f"BLEController: Váratlan hiba parancs küldésekor ({hex_command}): {e}")
                raise e
        else:
            # Ezt a hibát a hívónak (async_helper) kell elkapnia és a command_error_signal-ra küldenie
            raise BleakError("Cannot send command: Not connected to device.")
