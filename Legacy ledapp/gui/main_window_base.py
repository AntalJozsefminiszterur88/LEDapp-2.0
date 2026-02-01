# LEDapp/gui/main_window_base.py (Kiegészítve)

import threading  # Szükséges a threading.Event-hez
import time
import traceback

from PySide6.QtWidgets import (
    QApplication,
    QMainWindow,
    QWidget,
    QVBoxLayout,
    QLabel,
    QPushButton,
    QListWidget,
    QProgressBar,
    QMessageBox,
    QFrame,
    QSpacerItem,
    QSizePolicy,
    QSystemTrayIcon,  # QSystemTrayIcon hozzáadva
)
from PySide6.QtCore import (
    Qt,
    Signal,
    Slot,
    QMetaObject,
    QTimer,
)

# Importáljuk a szükséges konfigurációs és backend elemeket
try:
    from config import COLORS, DAYS, hex_to_command
    from core.ble_controller import BLEController
    from core.reconnect_handler import log_event  # Logolás
    from gui.async_helper import AsyncHelper
    from gui.gui_manager import GuiManager

    # GUI Widget importok itt is kellenek az isinstance miatt
    from gui.gui1_pyside import GUI1_Widget
    from gui.gui2_schedule_pyside import GUI2_Widget
    from gui import gui2_schedule_logic as schedule_logic

    # Új import a config kezelőhöz
    from core import config_manager
except ImportError as e:
    print(f"Hiba az importálás során main_window_base.py-ben: {e}")

    # Dummy log_event, ha a core import nem sikerül
    def log_event(msg):
        print(f"[LOG - Dummy BaseWindow]: {msg}")

    # Dummy config_manager, ha a core import nem sikerül
    class DummyConfigManager:
        @staticmethod
        def get_setting(key):
            return None

        @staticmethod
        def set_setting(key, value):
            pass

    config_manager = DummyConfigManager
    schedule_logic = None
    # Itt kiléphetnénk, vagy dummy osztályokat definiálhatnánk,
    # de a biztonság kedvéért most csak logolunk és megyünk tovább
    # sys.exit(1) # Kilépés hiba esetén

# Időzóna kezelés
import pytz

try:
    import tzlocal

    LOCAL_TZ = tzlocal.get_localzone()
    log_event(f"Helyi időzóna (tzlocal): {LOCAL_TZ.zone if LOCAL_TZ else 'Ismeretlen'}")
except Exception:
    log_event("tzlocal nem található vagy hiba történt, 'Europe/Budapest' használata.")
    try:
        LOCAL_TZ = pytz.timezone("Europe/Budapest")
        log_event(f"Helyi időzóna (fix): {LOCAL_TZ.zone}")
    except pytz.UnknownTimeZoneError:
        log_event("Figyelmeztetés: 'Europe/Budapest' időzóna sem található. UTC használata.")
        LOCAL_TZ = pytz.utc


class LEDApp_BaseWindow(QMainWindow):
    # --- Signals ---
    connection_status_signal = Signal(str)
    scan_results_signal = Signal(object)
    scan_error_signal = Signal(str)
    connect_results_signal = Signal(bool)
    connect_error_signal = Signal(str)
    command_error_signal = Signal(str)

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("LED-Irányító 2000")

        # --- Központi Widget és Layout Létrehozása ---
        self.central_widget = QWidget()
        self.main_layout = QVBoxLayout(self.central_widget)
        self.setCentralWidget(self.central_widget)

        # *** ÚJ: Event a reconnect loop leállításához ***
        self._stop_reconnect_event = threading.Event()
        # ********************************************

        # --- Segédosztályok Inicializálása ---
        self.async_helper = AsyncHelper(self)
        # Fontos, hogy a GuiManager megkapja az app példányt, amiben az event van
        self.gui_manager = GuiManager(self)

        # --- Változók ---
        self.last_user_input = time.time()
        self.devices = []  # Kezdetben üres lista
        # Kezdeti érték betöltése a configból
        self.selected_device = (
            (
                config_manager.get_setting("last_device_name"),
                config_manager.get_setting("last_device_address"),
            )
            if config_manager.get_setting("last_device_address")
            else None
        )
        self.connected = False  # Induláskor sosem csatlakozunk még
        self.last_color_hex = COLORS[0][2] if COLORS else None
        self.is_led_on = True
        self.latitude = 47.4338
        self.longitude = 19.1931
        self.sunrise = None
        self.sunset = None
        self.connection_status = "disconnected"
        self._current_gui_widget = None  # Ezt a GuiManager is látja/módosítja
        default_schedule = {
            day: {
                "color": "",
                "on_time": "",
                "off_time": "",
                "sunrise": False,
                "sunrise_offset": 0,
                "sunset": False,
                "sunset_offset": 0,
            }
            for day in DAYS
        }
        self.profiles = {"Alap": {"active": True, "schedule": default_schedule}}
        self.schedule = default_schedule

        # --- Load saved scheduling profiles on startup ---
        if schedule_logic is not None:
            try:
                schedule_logic.load_profiles_from_file(self)
                # If profiles were loaded, set the current schedule to the first profile
                if self.profiles:
                    first = next(iter(self.profiles.values()))
                    self.schedule = first.get("schedule", default_schedule)
            except Exception as e:
                log_event(f"Profilok betöltése sikertelen: {e}")
        self.ble = BLEController()
        self._is_auto_starting = False  # Új flag az automatikus indulás jelzésére
        self._initial_connection_attempted = False  # Új flag

        # --- GUI Indítása ---
        self.gui_manager._apply_stylesheet()
        # A GUI betöltése most már a main.py-ben történik a logika alapján
        # self.gui_manager.load_gui1() # Ezt kikommenteljük innen
        # self.gui_manager.center_window() # Ezt is áthelyezzük

        # --- Signalok összekötése ---
        self.connection_status_signal.connect(self.update_connection_status_gui)
        self.scan_results_signal.connect(self._handle_scan_results)
        self.scan_error_signal.connect(self._handle_scan_error)
        self.connect_results_signal.connect(self._handle_connect_results)
        self.connect_error_signal.connect(self._handle_connect_error)
        self.command_error_signal.connect(self._handle_command_error)

        # --- Globális ütemezés ellenőrző időzítő ---
        self.schedule_timer = QTimer(self)
        self.schedule_timer.timeout.connect(self._check_schedule_color)
        # Only start after a successful connection

    # *** ÚJ SLOT a disconnect utáni GUI1 töltéshez ***
    @Slot()
    def _load_gui1_slot(self):
        """Slot, amit a QMetaObject hívhat a GUI1 újratöltéséhez."""
        self.gui_manager.load_gui1()

    # ***********************************************

    @Slot()
    def disconnect_device(self):
        """Megszakítja a kapcsolatot, leállítja a reconnect loopot és visszatér a GUI1-re."""
        log_event("Kapcsolat bontása felhasználó által (disconnect_device)...")

        # 1. Állapotváltozók visszaállítása
        self.connected = False
        self.selected_device = None  # Kiválasztott eszköz törlése
        self.devices = []  # Eszközlista törlése
        # Utolsó eszköz törlése a konfigurációból is
        config_manager.set_setting("last_device_address", None)
        config_manager.set_setting("last_device_name", None)

        self.update_connection_status_gui("disconnected")  # GUI azonnali frissítése

        # Stop schedule timer when disconnecting
        if hasattr(self, "schedule_timer"):
            self.schedule_timer.stop()

        # 2. Reconnect Loop Leállítása (Signal az eventtel)
        log_event("Reconnect thread leállításának jelzése...")
        self._stop_reconnect_event.set()  # Event beállítása

        async def do_disconnect():
            try:
                if self.ble and self.ble.client:
                    addr = self.ble.client.address  # Mentsük el a címet logoláshoz
                    log_event(f"BLE disconnect kérése: {addr}")
                    await self.ble.disconnect()  # Ez bontja a kapcsolatot
                    log_event("BLE disconnect kérés sikeresen elküldve/befejezve.")
                else:
                    log_event("BLE disconnect nem szükséges (nincs kliens vagy már bontva).")
            except Exception as e:
                log_event(f"Hiba a kapcsolat bontásakor (async): {e}")
                traceback.print_exc()
            finally:
                # 3. GUI1 újratöltése a FŐ SZÁLON, miután a disconnect lefutott
                log_event("GUI1 újratöltésének kérése a disconnect után...")
                # Most már a dedikált slotot hívjuk meg a főablakon (self)
                QMetaObject.invokeMethod(self, "_load_gui1_slot", Qt.ConnectionType.QueuedConnection)

        # Aszinkron disconnect indítása az AsyncHelperrel
        self.async_helper.run_async_task(do_disconnect(), None, self.command_error_signal)

        # Azonnal frissítjük a GUI1 gombjait és listáját (ha éppen az látható)
        self.update_button_states_if_gui1()

    def update_button_states_if_gui1(self):
        """Segédfüggvény, ami frissíti a GUI1 állapotát, ha az az aktív widget."""
        if isinstance(self._current_gui_widget, GUI1_Widget):
            self._current_gui_widget.update_button_states()
            self._current_gui_widget.update_device_list()  # Az üres self.devices listát mutatja

    # --- Signal Handler Slotok ---
    @Slot(str)
    def update_connection_status_gui(self, status):
        self.connection_status = status
        current_widget = self._current_gui_widget

        # Ha a tálca ikon létezik, frissítsük a tooltipjét
        if hasattr(self, "tray_icon") and self.tray_icon:
            device_name_str = f": {self.selected_device[0]}" if self.selected_device and self.selected_device[0] else ""
            if status == "connected":
                tooltip = f"LED-Irányító 2000 (Csatlakozva{device_name_str})"
            elif status == "connecting":
                tooltip = f"LED-Irányító 2000 (Csatlakozás...{device_name_str})"
            else:
                tooltip = "LED-Irányító 2000 (Nincs kapcsolat)"
            self.tray_icon.setToolTip(tooltip)

        if isinstance(current_widget, GUI2_Widget):
            label = getattr(current_widget, "status_indicator_label", None)
            if label and label.isVisible():
                if status == "connected":
                    text, color = "Állapot: Csatlakoztatva", "lime"
                elif status == "connecting":
                    text, color = "Állapot: Csatlakozás...", "#FFA500"
                else:
                    text, color = "Állapot: Nincs kapcsolat", "#FF6B6B"
                label.setText(text)
                label.setStyleSheet(f"QLabel {{ color: {color}; font-weight: bold; background-color: transparent; }}")
        elif isinstance(current_widget, GUI1_Widget):
            progress_label = getattr(current_widget, "progress_label", None)
            if progress_label and progress_label.isVisible():
                if status == "connecting":
                    progress_label.setText("Csatlakozás...")
                elif status == "disconnected":
                    progress_label.setText("Kapcsolat bontva.")
                elif status == "connected":
                    progress_label.setText("Csatlakozva")  # GUI1-en is jelezzük

    @Slot(object)
    def _handle_scan_results(self, devices):
        log_event(
            f"_handle_scan_results SLOT triggered in GUI thread. Received data type: {type(devices)}, Value: {devices}"
        )
        current_widget = self._current_gui_widget
        if isinstance(current_widget, GUI1_Widget):
            if isinstance(devices, list):
                current_widget.on_scan_finished(devices)
            else:
                log_event(f"Hiba: Váratlan típus érkezett a scan eredményeként: {type(devices)}")
                current_widget.on_scan_error("Belső hiba: érvénytelen keresési eredmény.")
            current_widget.on_scan_finally()
        else:
            log_event("Figyelmeztetés: Scan eredmény érkezett, de nem a GUI1 aktív.")

    @Slot(str)
    def _handle_scan_error(self, error_message):
        log_event(f"_handle_scan_error SLOT triggered in GUI thread. Error: {error_message}")
        current_widget = self._current_gui_widget
        if isinstance(current_widget, GUI1_Widget):
            current_widget.on_scan_error(error_message)
            current_widget.on_scan_finally()
        else:
            log_event(f"Figyelmeztetés: Scan hiba ({error_message}), de nem a GUI1 aktív.")

    @Slot(bool)
    def _handle_connect_results(self, success):
        log_event(f"_handle_connect_results SLOT triggered in GUI thread. Success: {success}")
        current_widget = self._current_gui_widget
        self._initial_connection_attempted = True  # Jelöljük, hogy a kezdeti próbálkozás megtörtént

        if success:
            self.connected = True
            # *** Sikeres csatlakozáskor mentsük az eszközt ***
            if self.selected_device:
                config_manager.set_setting("last_device_address", self.selected_device[1])
                config_manager.set_setting("last_device_name", self.selected_device[0])
                log_event(f"Utolsó eszköz elmentve: {self.selected_device[0]} ({self.selected_device[1]})")
            else:
                log_event("Figyelmeztetés: Sikeres csatlakozás, de self.selected_device üres.")

            # Ha a GUI1 aktív, akkor váltsunk GUI2-re
            if isinstance(current_widget, GUI1_Widget):
                if not self.gui_manager.load_gui2():
                    self.connected = False  # Hiba esetén visszaállítjuk
                    current_widget.on_connect_finally()
                else:
                    if not self.schedule_timer.isActive():
                        self.schedule_timer.start(5000)
            else:
                # Ha nincs még GUI betöltve (pl. rejtett induláskor), töltsük be a GUI2-t
                if not self._initial_gui_loaded:
                    self.gui_manager.load_gui2()
                    self._initial_gui_loaded = True
                # Ha nem GUI1 volt aktív (pl. auto-connect), csak frissítsük az állapotot
                self.update_connection_status_gui("connected")
                if not self.schedule_timer.isActive():
                    self.schedule_timer.start(5000)

            # Automatikus indításkor nem jelenítjük meg az ablakot és nem küldünk értesítést
            # A GUI marad a háttérben betöltve, hogy később azonnal megjelenhessen

        else:  # Ha a csatlakozás sikertelen
            self.connected = False
            # Hiba esetén a _handle_connect_error fog lefutni
            if isinstance(current_widget, GUI1_Widget):
                current_widget.on_connect_finally()  # GUI1 gombjainak visszaállítása
            else:
                # Ha nem GUI1 aktív (pl. auto-connect hiba), frissítsük az állapotot
                self.update_connection_status_gui("disconnected")
                # Automatikus indításkor, ha sikertelen a csatlakozás és nincs GUI betöltve,
                # a GUI1-et kell megjeleníteni
                if self._is_auto_starting:
                    if not self._initial_gui_loaded:
                        self.gui_manager.load_gui1()
                        self._initial_gui_loaded = True
                        self.show()
                        self.raise_()
                        self.activateWindow()

    @Slot(str)
    def _handle_connect_error(self, error_message):
        log_event(f"_handle_connect_error SLOT triggered in GUI thread. Error: {error_message}")
        current_widget = self._current_gui_widget
        self.connected = False  # Biztosan nem vagyunk csatlakozva
        self._initial_connection_attempted = (
            True  # Jelöljük, hogy a kezdeti próbálkozás megtörtént (de sikertelen volt)
        )

        if isinstance(current_widget, GUI1_Widget):
            current_widget.on_connect_error(error_message)  # Hibaüzenet megjelenítése
            current_widget.on_connect_finally()  # Gombok visszaállítása
        else:
            log_event(f"Figyelmeztetés: Connect hiba ({error_message}), de nem a GUI1 aktív.")
            self.update_connection_status_gui("disconnected")
            if hasattr(self, "statusBar") and callable(self.statusBar):
                self.statusBar().showMessage(f"Kapcsolódási hiba: {error_message}", 5000)
            # Automatikus indítás esetén, ha sikertelen a csatlakozás és még nincs GUI betöltve,
            # töltsük be a GUI1-et és jelenítsük meg az ablakot
            if self._is_auto_starting:
                if not self._initial_gui_loaded:
                    self.gui_manager.load_gui1()
                    self._initial_gui_loaded = True
                    self.show()
                    self.raise_()
                    self.activateWindow()

    @Slot(str)
    def _handle_command_error(self, error_message):
        log_event(f"_handle_command_error SLOT triggered in GUI thread. Error: {error_message}")
        if hasattr(self, "statusBar") and callable(self.statusBar):
            self.statusBar().showMessage(f"Parancsküldési hiba: {error_message}", 5000)
        if "Not connected" in error_message or "disconnected" in error_message.lower():
            self.connected = False
            self.update_connection_status_gui("disconnected")
            # Ha parancsküldéskor derül ki, hogy nincs kapcsolat, és GUI2 van nyitva,
            # akkor visszadobhatnánk GUI1-re, de ezt a reconnect handlernek kellene kezelnie.
            # Lehet, hogy itt is jelezni kellene a felhasználónak egyértelműbben.
            if isinstance(self._current_gui_widget, GUI2_Widget):
                # Opcionális: Hibaüzenet a GUI2-n
                # QMessageBox.warning(self, "Kapcsolati Hiba", "Megszakadt a kapcsolat az eszközzel.")
                # Vagy hagyatkozunk a státuszjelzőre és a reconnect loopra.
                pass

    def _check_schedule_color(self):
        """Globálisan ellenőrzi az ütemezést és frissíti a LED színét."""
        if schedule_logic is None:
            return
        try:
            desired_hex = schedule_logic.get_color_by_visualization(self)
            log_event(f"_check_schedule_color: desired_hex={desired_hex}")
            cmd = hex_to_command(desired_hex) if desired_hex else None
            if cmd:
                # Always send the command even if the color seemingly matches.
                # This helps when the bulb ignored a previous command.
                log_event(f"Sending schedule color command: {cmd}")
                self.last_user_input = time.time()
                self.last_color_hex = cmd
                self.is_led_on = True
                self.async_helper.run_async_task(
                    self.ble.send_command(cmd),
                    callback_error_signal=self.command_error_signal,
                )
            else:
                if self.is_led_on:
                    log_event("Schedule indicates LED should be off")
                    self.last_user_input = time.time()
                    self.is_led_on = False
                    self.async_helper.run_async_task(
                        self.ble.send_command("7e00050300000000ef"),
                        callback_error_signal=self.command_error_signal,
                    )
        except Exception as e:
            log_event(f"Schedule timer error: {e}")

    def base_cleanup(self):
        """Alapvető cleanup műveletek kilépéskor."""
        log_event("Base cleanup műveletek indítása (kilépés)...")
        # Jelezzük a reconnect loopnak (ha még futna), hogy álljon le
        self._stop_reconnect_event.set()
        # Async hurok leállítását kérjük
        self.async_helper.stop_loop()
        if hasattr(self, "schedule_timer"):
            self.schedule_timer.stop()
        log_event("Base cleanup (stop kérések) befejezve.")
        # A szálak leállása és a loop bezárása a háttérben történik meg (daemon=True, stop())
