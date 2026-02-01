"""Entry point for the LED application.

This module sets up the Qt application, handles single instance logic and
provides the ``attempt_auto_connect`` coroutine used during startup.
"""

import sys
import os
import argparse
import asyncio  # Importálva
from PySide6.QtWidgets import QApplication
from PySide6.QtGui import QIcon
from PySide6.QtCore import QMetaObject, Qt, Q_ARG
from PySide6.QtNetwork import QLocalServer, QLocalSocket

# Importáljuk a szükséges modulokat
try:
    from gui.main_window_pyside import LEDApp_PySide
    from core import config_manager
    from core.reconnect_handler import log_event
except ImportError as e:

    def log_event(msg):
        """Fallback logger if imports fail."""
        print(f"[LOG - Dummy Main]: {msg}")

    log_event(f"Kritikus hiba az importálás során a main.py-ban: {e}")
    print(f"CRITICAL IMPORT ERROR in main.py: {e}", file=sys.stderr)
    sys.exit(1)

# Egyetlen példány azonosítója
SINGLE_INSTANCE_KEY = "LEDAppSingleton"


async def attempt_auto_connect(app_instance):
    """Megkísérli az automatikus csatlakozást a háttérben."""
    if not app_instance:
        return False

    # A visszatérési érték jelenleg nem használatos, de a jövőben
    # meghatározhatjuk a kapcsolat sikerességét is.

    try:
        # *** Opcionális extra késleltetés az újraindítás után ***
        log_event("Rövid várakozás az auto-connect előtt (Bluetooth inicializálás)...")
        await asyncio.sleep(2.0)  # Adjunk 2 másodpercet (ez állítható)
        # ****************************************************

        last_addr = config_manager.get_setting("last_device_address")
        last_name = config_manager.get_setting("last_device_name")

        if not last_addr or not last_name:
            log_event("Automatikus csatlakozás kihagyva: Nincs mentett utolsó eszköz.")
            return False  # Már itt visszatérünk

        log_event(f"Automatikus csatlakozás megkísérlése: {last_name} ({last_addr})")
        app_instance.selected_device = (last_name, last_addr)
        QMetaObject.invokeMethod(
            app_instance,
            "update_connection_status_gui",
            Qt.ConnectionType.QueuedConnection,
            Q_ARG(str, "connecting"),
        )

        # Csatlakozási kísérlet indítása az AsyncHelper segítségével
        # Az eredményt a done_callback fogja kezelni és a signalokat kibocsátani
        future = app_instance.async_helper.run_async_task(
            app_instance.ble.connect_with_retry(last_addr),
            app_instance.connect_results_signal,
            app_instance.connect_error_signal,
        )

        # !!! A HIBÁS RÉSZ ELTÁVOLÍTVA !!!
        # Nincs szükség az await asyncio.wait_for(future, ...)-ra itt,
        # mert az AsyncHelper done_callback-je kezeli az eredményt/hibát.
        # A future objektummal itt nem csinálunk semmit.

        if not future:
            log_event("Hiba: Nem sikerült elindítani az automatikus csatlakozási" " taskot.")
        # A kapcsolat sikerességét a signal handler (_handle_connect_results)
        # fogja beállítani, ezért itt nem tudjuk szinkron módon megvárni az
        # eredményt.
        # A _initial_connection_attempted flag beállítása a finally blokkban történik.

    except Exception as e:
        # Ez a blokk valószínűleg nem fog lefutni a wait_for eltávolítása miatt,
        # de biztonság kedvéért itt hagyjuk.
        log_event(f"Váratlan hiba az attempt_auto_connect indításakor: {e}")
        # Hibajelzés küldése a fő szálra
        if hasattr(app_instance, "_handle_connect_error"):
            error_msg = f"Indítási hiba: {e}"
            QMetaObject.invokeMethod(
                app_instance,
                "_handle_connect_error",
                Qt.ConnectionType.QueuedConnection,
                Q_ARG(str, error_msg),
            )

    finally:
        app_instance._initial_connection_attempted = True
        # A sikeresség itt már nem releváns, mert a háttérben dől el.
        log_event("_initial_connection_attempted flag beállítva: True (Csatlakozás a" " háttérben fut/futott).")
        # Az ablak betöltését a show_window_from_tray vagy a _handle_connect_results intézi.

    # A függvény visszatérési értéke itt már nem tükrözi a kapcsolat sikerességét,
    # mivel az aszinkron módon dől el.
    return None  # Vagy True, ha a task indítása sikeres volt, de ez félrevezető lehet.


if __name__ == "__main__":
    # --- Parancssori argumentumok feldolgozása ---
    parser = argparse.ArgumentParser()
    parser.add_argument("--tray", action="store_true", help="Indítás rejtve a tálcára.")
    args = parser.parse_args()

    # --- Qt Alkalmazás Inicializálása ---
    qt_app = QApplication(sys.argv)

    # --- Egyetlen példány ellenőrzése ---
    _instance_server = QLocalServer()
    _instance_socket = QLocalSocket()
    _instance_socket.connectToServer(SINGLE_INSTANCE_KEY)
    if _instance_socket.waitForConnected(100):
        _instance_socket.write(b"activate")
        _instance_socket.flush()
        _instance_socket.waitForBytesWritten(100)
        _instance_socket.disconnectFromServer()
        sys.exit(0)
    else:
        _instance_socket.abort()
        if not _instance_server.listen(SINGLE_INSTANCE_KEY):
            QLocalServer.removeServer(SINGLE_INSTANCE_KEY)
            _instance_server.listen(SINGLE_INSTANCE_KEY)

    # --- Ikon Beállítása ---
    # ... (ikon betöltési logika változatlan) ...
    ICON_PATH = "led_icon.ico"
    app_icon = QIcon()
    if os.path.exists(ICON_PATH):
        app_icon = QIcon(ICON_PATH)
    elif getattr(sys, "frozen", False):
        base_path = getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
        frozen_icon_path = os.path.join(base_path, ICON_PATH)
        if os.path.exists(frozen_icon_path):
            app_icon = QIcon(frozen_icon_path)
        else:
            print(f"Figyelmeztetés: Az ikonfájl nem található a fagyasztott helyen sem:" f" {frozen_icon_path}")
    else:
        print(f"Figyelmeztetés: Az ikonfájl nem található: {ICON_PATH}")

    if not app_icon.isNull():
        qt_app.setWindowIcon(app_icon)

    # --- Ablak bezárásának kezelése ---
    qt_app.setQuitOnLastWindowClosed(False)

    # --- Főablak létrehozása ---
    start_hidden_arg = args.tray
    main_window = LEDApp_PySide(start_hidden=start_hidden_arg)
    main_window._is_auto_starting = start_hidden_arg

    def _bring_existing_to_front():
        if main_window.isHidden():
            main_window.show_window_from_tray()
        else:
            main_window.showNormal()
            main_window.raise_()
            main_window.activateWindow()

    # új példány értesítések kezelése
    def _handle_new_connection():
        conn = _instance_server.nextPendingConnection()
        if conn:
            conn.waitForReadyRead(100)
            conn.readAll()
            conn.disconnectFromServer()
            _bring_existing_to_front()

    _instance_server.newConnection.connect(_handle_new_connection)

    if not app_icon.isNull():
        main_window.setWindowIcon(app_icon)

    # --- Automatikus csatlakozás és indítási logika ---
    if start_hidden_arg:
        log_event("Indítás --tray argumentummal.")

        if config_manager.get_setting("auto_connect_on_startup"):

            async def delayed_autoconnect():
                """Indít egy rövid késleltetés után automatikus csatlakozást."""
                # Rövid várakozás az eseményhurok stabilizálódására
                await asyncio.sleep(0.5)
                # Elindítja a csatlakozást
                await attempt_auto_connect(main_window)

            # Az asyncio task futtatása az AsyncHelperen keresztül
            main_window.async_helper.run_async_task(delayed_autoconnect())
        else:
            log_event("Automatikus csatlakozás kihagyva (beállítás szerint le van tiltva).")
            # Jelöljük, hogy nem kell várni
            main_window._initial_connection_attempted = True

    else:
        log_event("Normál indítás.")
        # Normál indításnál nincs auto-connect kísérlet
        main_window._initial_connection_attempted = True
        main_window.show()

    # --- Qt Eseményhurok Indítása ---
    sys.exit(qt_app.exec())
