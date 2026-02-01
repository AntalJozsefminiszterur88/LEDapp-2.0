# LEDapp/gui/gui_manager.py (Javított clear_window_content)

import sys
import threading
import asyncio
import traceback

from PySide6.QtWidgets import (
    QApplication,
    QMainWindow,
    QVBoxLayout,
    QMessageBox,
)

# Importok
try:
    from gui.gui1_pyside import GUI1_Widget
    from gui.gui2_schedule_pyside import GUI2_Widget
    from core.reconnect_handler import log_event
except ImportError as e:
    # Dummy log_event, ha a core import nem sikerül
    def log_event(msg):
        print(f"[LOG - Dummy GuiManager]: {msg}")

    log_event(f"HIBA: Nem sikerült importálni a szükséges modulokat a gui_manager.py-ban: {e}")
    # Itt nem lépünk ki, de a program valószínűleg nem fog működni helyesen
    # sys.exit(1)


class GuiManager:
    """Segédosztály a GUI megjelenítésének és váltásának kezelésére."""

    def __init__(self, app_instance: QMainWindow):
        self.app = app_instance
        self.reconnect_thread = None
        self.central_widget = self.app.centralWidget()
        self.main_layout = self.central_widget.layout()
        if not self.main_layout:
            self.main_layout = QVBoxLayout(self.central_widget)
            log_event("Figyelmeztetés: GuiManager init, layout létrehozva.")

    def _apply_stylesheet(self):
        """Alkalmazza a központi stíluslapot."""
        # Cseréld le a teljes stíluslap tartalmára!
        self.app.setStyleSheet(
            """
            QMainWindow, QWidget {
                background-color: rgb(50, 51, 57);
                color: #E0E0E0;
            }
            QLabel { background-color: transparent; font-family: Arial; color: #E0E0E0; }
            QLabel#titleLabel { font-size: 16pt; font-weight: bold; color: #FFFFFF; }
            QLabel#statusLabel { font-size: 11pt; font-weight: bold; }
            QLabel#positionStatusLabel { font-size: 10pt; font-weight: bold; } /* Szín itt törölve, dinamikusan lesz beállítva */
            QPushButton {
                font-family: Arial; font-size: 11pt; padding: 5px 10px;
                min-height: 2em; min-width: 7em; border: 1px solid #777;
                border-radius: 4px; background-color: #555; color: #FFFFFF;
            }
            QPushButton:hover { background-color: #666; }
            QPushButton:pressed { background-color: #444; }
            QPushButton:disabled { background-color: #404040; color: #888; border-color: #666; }
            QPushButton#powerOffButton { background-color: #A03030; }
            QPushButton#powerOffButton:hover { background-color: #B04040; }
            QPushButton#powerOffButton:pressed { background-color: #902020; }
            QPushButton#powerOnButton { background-color: #308030; }
            QPushButton#powerOnButton:hover { background-color: #409040; }
            QPushButton#powerOnButton:pressed { background-color: #207020; }
            QPushButton#addProfileButton,
            QPushButton#deleteProfileButton,
            QPushButton#customColorButton {
                min-width: 5em;
                min-height: 1.6em;
            }
            QListWidget {
                font-family: Arial; font-size: 11pt; background-color: #444;
                color: #E0E0E0; border: 1px solid #777;
                selection-background-color: #0078D7; selection-color: white;
            }
            QProgressBar {
                 border: 1px solid #777; border-radius: 5px; text-align: center;
                 background-color: #444; height: 18px; color: #E0E0E0;
            }
             QProgressBar::chunk { background-color: #308030; border-radius: 4px; }
             QComboBox {
                 font-family: Arial; min-height: 1.8em; padding: 1px 5px 1px 3px;
                 border: 1px solid #777; border-radius: 3px; background-color: #555;
                 color: #E0E0E0; selection-background-color: #0078D7;
             }
            QComboBox::drop-down { border: none; }
            QComboBox::down-arrow {
                width: 0; height: 0; margin-right: 6px;
                border-left: 5px solid transparent;
                border-right: 5px solid transparent;
                border-top: 7px solid #E0E0E0;
            }
            QComboBox QAbstractItemView {
                background-color: #555; border: 1px solid #777;
                selection-background-color: #0078D7; color: #E0E0E0;
            }
             QLineEdit {
                 font-family: Arial; min-height: 1.8em; padding: 1px 3px;
                 border: 1px solid #777; border-radius: 3px;
                 background-color: #555; color: #E0E0E0;
             }
             QCheckBox { spacing: 5px; background-color: transparent;}
             QCheckBox::indicator {
                width: 15px; height: 15px; border: 1px solid #888;
                border-radius: 3px; background-color: #444;
             }
             QCheckBox::indicator:checked { background-color: #308030; image: none; }
             QGridLayout { spacing: 5px; }
         """
        )

    def center_window(self):
        """Ablakot képernyő közepére igazítja."""
        if QApplication.instance():
            try:
                screen = QApplication.primaryScreen()
                if screen:
                    screen_geometry = screen.availableGeometry()
                    window_geometry = self.app.frameGeometry()
                    x = (screen_geometry.width() - window_geometry.width()) // 2
                    y = (screen_geometry.height() - window_geometry.height()) // 2
                    x = max(0, x)
                    y = max(0, y)
                    self.app.move(x, y)
                else:
                    log_event("Hiba: Nem található elsődleges képernyő.")
            except Exception as e:
                log_event(f"Hiba az ablak középre igazítása közben: {e}")

    def _clear_layout(self, layout):
        """Rekurzív layout törlő (fallback)."""
        if layout is not None:
            while layout.count():
                item = layout.takeAt(0)
                widget = item.widget()
                if widget is not None:
                    if hasattr(widget, "stop_timers") and callable(widget.stop_timers):
                        try:
                            widget.stop_timers()
                        except Exception as e:
                            log_event(f"Hiba a widget stop_timers hívásakor (_clear_layout): {e}")
                    widget.deleteLater()
                else:
                    sub_layout = item.layout()
                    if sub_layout is not None:
                        self._clear_layout(sub_layout)

    def clear_window_content(self):
        """Törli az aktuálisan megjelenített GUI widgetet."""
        current_widget = self.app._current_gui_widget
        if isinstance(current_widget, GUI2_Widget):
            log_event("clear_window_content: GUI2 volt aktív, reconnect loop stop jelzés...")
            if hasattr(self.app, "_stop_reconnect_event"):
                self.app._stop_reconnect_event.set()
            self.reconnect_thread = None

        if current_widget:
            log_event(f"clear_window_content: Aktuális widget törlése: {current_widget.objectName()}")
            if hasattr(current_widget, "stop_timers") and callable(current_widget.stop_timers):
                try:
                    current_widget.stop_timers()
                except Exception as e:
                    log_event(f"Hiba a {current_widget.objectName()} stop_timers hívásakor: {e}")

            current_widget.setParent(None)
            current_widget.deleteLater()
            self.app._current_gui_widget = None
        else:
            log_event("clear_window_content: Nem volt aktuális widget referencia, layout ürítése fallbackként.")
            self._clear_layout(self.main_layout)  # Fallback

    def load_gui1(self):
        """Betölti az első képernyőt."""
        self.clear_window_content()  # Ez leállítja a reconnect loopot is, ha kell
        self.app.setWindowTitle("LED-Irányító 2000 - Csatlakozás")
        self.app.resize(600, 450)

        widget = GUI1_Widget(self.app)  # Fő app példány átadása
        self.main_layout.addWidget(widget)
        self.app._current_gui_widget = widget  # Referencia beállítása

        # GUI1 állapotának frissítése (üres lista, megfelelő gombok)
        widget.update_button_states()
        widget.update_device_list()  # Az app.devices lista alapján (ami most üres kell legyen)

        self.center_window()

    def load_gui2(self):
        """Betölti a második képernyőt és elindítja a reconnect loopot."""
        if not self.app.selected_device:
            log_event("Hiba: Nincs kiválasztott eszköz a GUI2 betöltésekor.")
            QMessageBox.warning(self.app, "Hiba", "Nincs kiválasztott eszköz.")
            return False

        self.clear_window_content()  # Ez most GUI1-et töröl, nem állít le loopot
        self.app.setWindowTitle(f"LED-Irányító 2000 - {self.app.selected_device[0]}")
        # Default width adjusted so the schedule view still fits comfortably
        self.app.resize(950, 700)

        widget = GUI2_Widget(self.app)  # Fő app példány átadása
        self.main_layout.addWidget(widget)
        self.app._current_gui_widget = widget  # Referencia beállítása

        self.app.update_connection_status_gui(self.app.connection_status)  # GUI állapot frissítése
        self.center_window()

        # Reconnect thread indítása
        if self.reconnect_thread is None or not self.reconnect_thread.is_alive():
            log_event("Reconnect thread indítása (GuiManager)...")
            if hasattr(self.app, "_stop_reconnect_event"):
                self.app._stop_reconnect_event.clear()  # Stop jelzés törlése
            else:
                log_event("HIBA: Nincs _stop_reconnect_event az app példányon GUI2 töltésekor!")
                # Valami komoly gond van, ha ez nincs meg itt
                return False

            self.reconnect_thread = threading.Thread(
                target=self._run_reconnect_loop_target,
                args=(
                    self.app,
                    self.app._stop_reconnect_event,
                ),  # Átadjuk az appot és az eventet
                daemon=True,
            )
            self.reconnect_thread.start()
        return True

    @staticmethod
    def _run_reconnect_loop_target(app_instance, stop_event):
        """Külön szálon futó asyncio hurok a kapcsolattartáshoz."""
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        try:
            log_event("Reconnect loop indítása...")
            # Itt a start_ble_connection_loop-ot kell futtatni a core modulból
            from core.reconnect_handler import start_ble_connection_loop  # Import itt

            loop.run_until_complete(start_ble_connection_loop(app_instance, stop_event))
        except Exception as e:
            log_event(f"Hiba a reconnect loop futtatása közben: {e}")
            traceback.print_exc()
        finally:
            log_event("Reconnect loop befejeződött.")
            if loop.is_running():
                try:
                    if sys.version_info >= (3, 7):
                        loop.run_until_complete(loop.shutdown_asyncgens())
                    loop.call_soon_threadsafe(loop.stop)
                except RuntimeError as e:
                    log_event(f"RuntimeError during reconnect loop stop: {e}")
