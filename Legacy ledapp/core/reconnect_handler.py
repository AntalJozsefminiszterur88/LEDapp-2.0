# LEDapp/core/reconnect_handler.py (Stop eventtel kiegészítve)
import asyncio
import time
from datetime import datetime
from bleak import BleakClient, BleakScanner, BleakError
import traceback
import threading  # Szükséges az Event-hez

# Konstansok (A gyorsított verziót használjuk)
CHARACTERISTIC_UUID = "0000fff3-0000-1000-8000-00805f9b34fb"
KEEP_ALIVE_COMMAND = "7e00000000000000ef"
LOG_FILE = "led_connection_log.txt"
CONNECT_TIMEOUT = 10.0  # gyorsabb timeout a connect hívásokhoz
PING_INTERVAL = 20.0
INACTIVITY_PING_THRESHOLD = 5.0
RECONNECT_DELAY = 1.0
MAX_CONNECT_ATTEMPTS = 3
RESCAN_DELAY = 5.0
LOOP_SLEEP = 0.5
FAST_FIND_TIMEOUT = 3.0  # gyors cím alapú keresés ideje
RESCAN_TIMEOUT = 8.0  # rövidebb újrakeresési idő
# POST_RESCAN_CONNECT_DELAY itt nincs


def log_event(message):
    """Esemény logolása konzolra."""
    timestamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    entry = f"[{timestamp}] {message}"
    print(entry)


async def rescan_and_find_device(target_name):
    """Új keresést végez és megkeresi az eszközt név alapján, címet ad vissza."""
    log_event(f"Új keresés indítása a(z) '{target_name}' nevű eszközhöz...")
    try:
        devices = await BleakScanner.discover(timeout=RESCAN_TIMEOUT)
        for device in devices:
            if device.name == target_name:
                log_event(f"Eszköz újra megtalálva: {device.name} ({device.address})")
                return device.address
        log_event(f"'{target_name}' nevű eszköz nem található a keresés során.")
        return None
    except asyncio.CancelledError:
        log_event("Figyelmeztetés: Az eszközkeresés megszakadt (CancelledError).")
        return None
    except Exception as e:
        log_event(f"Hiba az újrakeresés során: {e}")
        return None


async def quick_find_by_address(address):
    """Rövid keresés egy adott címre, ha a kötés megszakadt."""
    log_event(f"Gyors címkeresés: {address}...")
    try:
        dev = await BleakScanner.find_device_by_address(address, timeout=FAST_FIND_TIMEOUT)
        if dev:
            log_event("Eszköz megtalálva gyors kereséssel.")
            return address
        log_event("Gyors keresés nem talált eszközt.")
        return None
    except asyncio.CancelledError:
        log_event("Gyors címkeresés megszakadt (CancelledError).")
        return None
    except Exception as e:
        log_event(f"Hiba a gyors címkeresés során: {e}")
        return None


# *** Függvény szignatúra bővítése a stop_eventtel ***
async def start_ble_connection_loop(app, stop_event: threading.Event):
    # *************************************************

    if not app.selected_device or not app.selected_device[0]:
        log_event("Hiba: Nincs kiválasztott eszköznév a kapcsolattartáshoz. Loop leáll.")
        return

    original_device_name = app.selected_device[0]
    current_address = app.selected_device[1]
    log_event(f"Kapcsolat figyelő indítása: '{original_device_name}' ({current_address})")
    last_ping_time = time.time()
    connection_attempts = 0

    while True:
        # *** STOP EVENT ELLENŐRZÉSE A CIKLUS ELEJÉN ***
        if stop_event.is_set():
            log_event("Stop event észlelve, reconnect loop leállítása...")
            break
        # *********************************************

        try:
            current_client = app.ble.client if hasattr(app, "ble") and app.ble else None

            if not current_client or not current_client.is_connected:
                if app.connection_status != "disconnected":
                    if hasattr(app, "connection_status_signal"):
                        app.connection_status_signal.emit("disconnected")
                    app.connection_status = "disconnected"

                log_event(
                    f"Kapcsolat ellenőrzés: Nincs kapcsolat '{original_device_name}' ({current_address}). Próba #{connection_attempts + 1}..."
                )

                # --- Újrakeresés logika ---
                if connection_attempts >= MAX_CONNECT_ATTEMPTS:
                    log_event("Maximum csatlakozási kísérlet elérve, újrakeresés...")
                    new_address = await rescan_and_find_device(original_device_name)

                    connection_attempts = 0
                    if new_address:
                        if new_address != current_address:
                            log_event(f"Eszköz új címen található: {new_address}")
                            current_address = new_address
                            app.selected_device = (
                                original_device_name,
                                current_address,
                            )
                        else:
                            log_event("Eszköz ugyanazon a címen található.")
                    else:
                        log_event(f"Eszköz nem található keresés után sem. Várakozás ({RESCAN_DELAY}s)...")
                        await asyncio.sleep(RESCAN_DELAY)
                        continue

                # --- Csatlakozási kísérlet ---
                try:
                    if hasattr(app, "connection_status_signal"):
                        app.connection_status_signal.emit("connecting")
                    app.connection_status = "connecting"

                    if app.ble and app.ble.client:
                        log_event("Régi app.ble.client bontásának kísérlete...")
                        old_client_ref = app.ble.client
                        app.ble.client = None
                        try:
                            if old_client_ref.is_connected:
                                await old_client_ref.disconnect()
                            log_event("Régi kliens bontva.")
                        except Exception as disconn_err:
                            log_event(f"Figyelmeztetés: Hiba a régi kliens bontásakor: {disconn_err}")

                    log_event(f"Új BleakClient létrehozása és hozzárendelése: {current_address}...")
                    client = BleakClient(current_address)
                    app.ble.client = client

                    log_event(f"Csatlakozás megkezdése: {current_address} (timeout={CONNECT_TIMEOUT}s)...")
                    await client.connect(timeout=CONNECT_TIMEOUT)

                    if hasattr(app, "connection_status_signal"):
                        app.connection_status_signal.emit("connected")
                    app.connection_status = "connected"
                    log_event(f"Sikeresen csatlakozva: '{original_device_name}' ({current_address})")
                    last_ping_time = time.time()
                    connection_attempts = 0

                except (BleakError, asyncio.TimeoutError, asyncio.CancelledError) as e:
                    log_event(f"Kapcsolódási hiba #{connection_attempts + 1} ({type(e).__name__}): {e}")
                    if hasattr(app, "connection_status_signal"):
                        app.connection_status_signal.emit("disconnected")
                    app.connection_status = "disconnected"

                    client_that_failed = app.ble.client
                    if client_that_failed:
                        log_event("Kapcsolódási hiba utáni kliens bontás kísérlete (app.ble.client)...")
                        app.ble.client = None
                        try:
                            await client_that_failed.disconnect()
                        except Exception as disconn_err_on_fail:
                            log_event(f"Figyelmeztetés: Hiba a kliens bontásakor hiba után: {disconn_err_on_fail}")

                    # gyors címellenőrzés, hátha a hirdetés már elérhető
                    try:
                        found = await quick_find_by_address(current_address)
                        if found:
                            connection_attempts = 0
                            await asyncio.sleep(RECONNECT_DELAY)
                            continue
                    except Exception as quick_err:
                        log_event(f"Gyors címellenőrzés hiba: {quick_err}")

                    connection_attempts += 1
                    # *** STOP EVENT ELLENŐRZÉSE HIBA ESETÉN IS ***
                    if stop_event.is_set():
                        log_event("Stop event észlelve hiba után, loop leáll.")
                        break
                    # ********************************************
                    await asyncio.sleep(RECONNECT_DELAY)
                    continue

                except Exception as e:
                    log_event(f"Általános hiba a kapcsolatban #{connection_attempts + 1}: {e}")
                    log_event(f"Traceback:\n{traceback.format_exc()}")
                    if hasattr(app, "connection_status_signal"):
                        app.connection_status_signal.emit("disconnected")
                    app.connection_status = "disconnected"
                    if app.ble:
                        app.ble.client = None
                    try:
                        found = await quick_find_by_address(current_address)
                        if found:
                            connection_attempts = 0
                            await asyncio.sleep(RECONNECT_DELAY)
                            continue
                    except Exception as quick_err:
                        log_event(f"Gyors címellenőrzés hiba: {quick_err}")

                    connection_attempts += 1
                    # *** STOP EVENT ELLENŐRZÉSE HIBA ESETÉN IS ***
                    if stop_event.is_set():
                        log_event("Stop event észlelve általános hiba után, loop leáll.")
                        break
                    # ********************************************
                    await asyncio.sleep(RECONNECT_DELAY)
                    continue

            # --- Ha Csatlakozva van: Keep-Alive Ping ---
            else:  # current_client and current_client.is_connected
                if app.connection_status != "connected":
                    log_event("Kliens csatlakozva, de app státusz nem 'connected'. Státusz frissítése.")
                    if hasattr(app, "connection_status_signal"):
                        app.connection_status_signal.emit("connected")
                    app.connection_status = "connected"

                now = time.time()
                last_input_time = app.last_user_input if hasattr(app, "last_user_input") else now
                elapsed_since_last_ping = now - last_ping_time
                elapsed_since_input = now - last_input_time

                should_ping = elapsed_since_last_ping >= PING_INTERVAL or (
                    elapsed_since_input >= INACTIVITY_PING_THRESHOLD
                    and elapsed_since_last_ping >= INACTIVITY_PING_THRESHOLD
                )

                if should_ping:
                    try:
                        if current_client and current_client.is_connected:
                            await current_client.write_gatt_char(
                                CHARACTERISTIC_UUID,
                                bytes.fromhex(KEEP_ALIVE_COMMAND),
                                response=False,
                            )
                            last_ping_time = time.time()
                            connection_attempts = 0
                        else:
                            log_event("Ping kihagyva, a kliens már nem csatlakozik (pingelés előtt ellenőrizve).")

                    except (BleakError, asyncio.CancelledError) as e:
                        log_event(f"Hiba ping küldésekor ({type(e).__name__}): {e}")
                        if hasattr(app, "connection_status_signal"):
                            app.connection_status_signal.emit("disconnected")
                        app.connection_status = "disconnected"
                        ping_failed_client = app.ble.client
                        if ping_failed_client:
                            log_event("Ping hiba utáni kliens bontás kísérlete...")
                            app.ble.client = None
                            try:
                                await ping_failed_client.disconnect()
                            except Exception as disconn_err_ping:
                                log_event(f"Hiba a kliens bontásakor ping hiba után: {disconn_err_ping}")
                        connection_attempts += 1
                        # *** STOP EVENT ELLENŐRZÉSE HIBA ESETÉN IS ***
                        if stop_event.is_set():
                            log_event("Stop event észlelve ping hiba után, loop leáll.")
                            break
                        # ********************************************
                        continue
                    except Exception as e:
                        log_event(f"Általános hiba ping küldésekor: {e}")
                        log_event(f"Traceback:\n{traceback.format_exc()}")
                        if hasattr(app, "connection_status_signal"):
                            app.connection_status_signal.emit("disconnected")
                        app.connection_status = "disconnected"
                        if app.ble:
                            app.ble.client = None
                        connection_attempts += 1
                        # *** STOP EVENT ELLENŐRZÉSE HIBA ESETÉN IS ***
                        if stop_event.is_set():
                            log_event("Stop event észlelve általános ping hiba után, loop leáll.")
                            break
                        # ********************************************
                        await asyncio.sleep(0.5)
                        continue

            # *** STOP EVENT ELLENŐRZÉSE A CIKLUS VÉGÉN IS ***
            if stop_event.is_set():
                log_event("Stop event észlelve (ciklus végén), reconnect loop leállítása...")
                break
            # ************************************************

            await asyncio.sleep(LOOP_SLEEP)

        except asyncio.CancelledError:
            log_event("A start_ble_connection_loop fő ciklusa megszakadt (CancelledError). Loop leáll.")
            break

        except Exception as e:
            log_event(f"Váratlan hiba a start_ble_connection_loop fő ciklusában: {e}")
            log_event(f"Traceback:\n{traceback.format_exc()}")
            if app.ble:
                app.ble.client = None
            app.connection_status = "disconnected"
            if hasattr(app, "connection_status_signal"):
                app.connection_status_signal.emit("disconnected")
            # *** STOP EVENT ELLENŐRZÉSE VÁRATLAN HIBA ESETÉN IS ***
            if stop_event.is_set():
                log_event("Stop event észlelve váratlan hiba után, loop leáll.")
                break
            # ****************************************************
            await asyncio.sleep(LOOP_SLEEP * 2)

    # Loop végén cleanup
    log_event("start_ble_connection_loop vége (while ciklusból kilépve), utolsó cleanup...")
    final_client = app.ble.client
    if final_client and final_client.is_connected:
        try:
            # Ideiglenes event loop disconnecthez - ez a rész maradhat
            try:
                loop = asyncio.get_running_loop()
                if loop.is_running():
                    await final_client.disconnect()
                else:
                    raise RuntimeError("Loop not running")
            except RuntimeError:
                log_event("Nincs futó loop a disconnecthez, új létrehozása...")
                try:
                    asyncio.run(final_client.disconnect())
                except Exception as e_run_disconn:
                    log_event(f"Hiba az új loopban futtatott disconnect során: {e_run_disconn}")
            log_event("Kliens bontva a loop végén.")
        except Exception as final_disconn_err:
            log_event(f"Hiba a kliens bontásakor a loop végén: {final_disconn_err}")
    if app.ble:
        app.ble.client = None
