# LEDapp/gui/gui2_schedule_pyside.py (Visszaállított kinézettel)

from datetime import datetime
import traceback
import pytz

from PySide6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QGridLayout,
    QComboBox,
    QLineEdit,
    QCheckBox,
    QSizePolicy,
    QMessageBox,
    QInputDialog,  # QGroupBox eltávolítva
    QScrollArea,
)
from PySide6.QtCore import Qt, QTimer, Slot
from PySide6.QtGui import QFont

# --- Logolás ---
try:
    from core.reconnect_handler import log_event
except ImportError:

    def log_event(msg):
        print(f"[LOG - Dummy GUI2Schedule]: {msg}")

    log_event("Figyelmeztetés: core.reconnect_handler.log_event import sikertelen.")

# --- Modul Importok ---
try:
    from config import COLORS, DAYS
    import core.config_manager as config_manager
    import core.registry_utils as registry_utils
    from core.sun_logic import DAYS_HU
    from core.location_utils import LOCAL_TZ
    from gui import gui2_schedule_logic as logic
    from gui.gui2_controls_pyside import GUI2_ControlsWidget
    from gui.custom_color_dialog import CustomColorDialog
    from gui.timeline_widget import TimelineWidget

    logic.LOCAL_TZ = LOCAL_TZ
    log_event("GUI2Schedule: Szükséges modulok sikeresen importálva.")

except ImportError as e:
    log_event(f"KRITIKUS HIBA: Nem sikerült importálni a szükséges modulokat gui2_schedule_pyside.py-ban: {e}")
    traceback.print_exc()

    class DummyManager:
        DEFAULT_SETTINGS = {
            "start_with_windows": False,
            "last_device_address": None,
            "last_device_name": None,
            "auto_connect_on_startup": True,
        }

        @staticmethod
        def get_setting(key):
            return DummyManager.DEFAULT_SETTINGS.get(key)

        @staticmethod
        def set_setting(key, value):
            pass

        @staticmethod
        def is_in_startup():
            return False

        @staticmethod
        def add_to_startup():
            pass

        @staticmethod
        def remove_from_startup():
            pass

    if "config_manager" not in globals():
        config_manager = DummyManager
    if "registry_utils" not in globals():
        registry_utils = DummyManager
    if "logic" not in globals():

        class DummyLogic:
            LOCAL_TZ = pytz.utc

            @staticmethod
            def load_profiles_from_file(app):
                pass

            @staticmethod
            def save_profile(widget):
                pass

            @staticmethod
            def check_profiles(widget):
                pass

            @staticmethod
            def get_local_sun_info():
                return {
                    "latitude": 0,
                    "longitude": 0,
                    "sunrise": None,
                    "sunset": None,
                    "located": False,
                }

        logic = DummyLogic()
    if "GUI2_ControlsWidget" not in globals():
        from PySide6.QtWidgets import QLabel as _QLabel

        def GUI2_ControlsWidget(app):
            return _QLabel("Vezérlő betöltési hiba")

    if "DAYS_HU" not in globals():
        DAYS_HU = {}
    if "COLORS" not in globals():
        COLORS = []
    if "DAYS" not in globals():
        DAYS = []
    if "LOCAL_TZ" not in globals():
        LOCAL_TZ = pytz.utc


class NoWheelComboBox(QComboBox):
    """QComboBox that ignores mouse wheel events to prevent accidental changes."""

    def wheelEvent(self, event):  # pragma: no cover - GUI behavior
        event.ignore()


class NoWheelLineEdit(QLineEdit):
    """QLineEdit that ignores mouse wheel events to prevent accidental changes."""

    def wheelEvent(self, event):  # pragma: no cover - GUI behavior
        event.ignore()


# --- Osztály Definíció ---
class GUI2_Widget(QWidget):
    def __init__(self, main_app, parent=None):
        super().__init__(parent)
        self.setObjectName("GUI2_Widget_Instance")
        self.main_app = main_app

        # --- Scrollable content area ---
        outer_layout = QVBoxLayout(self)
        scroll_area = QScrollArea()
        scroll_area.setWidgetResizable(True)
        outer_layout.addWidget(scroll_area)

        scroll_content = QWidget()
        scroll_area.setWidget(scroll_content)

        # --- Fő vertikális layout ---
        main_layout = QVBoxLayout(scroll_content)
        main_layout.setContentsMargins(10, 5, 10, 10)  # Kisebb felső margó
        main_layout.setSpacing(5)  # Kisebb alap térköz

        # --- Felső Sáv ---
        top_bar_layout = QHBoxLayout()
        # ... (Felső sáv tartalma változatlan) ...
        # Bal: Eszköznév és Állapot
        top_left_widget = QWidget()
        top_left_layout = QVBoxLayout(top_left_widget)
        top_left_layout.setContentsMargins(0, 0, 0, 0)
        top_left_layout.setAlignment(Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignLeft)
        device_name = self.main_app.selected_device[0] if self.main_app.selected_device else "Ismeretlen"
        device_label = QLabel(f"Csatlakoztatott eszköz: {device_name}")
        device_label.setFont(QFont("Arial", 12))
        top_left_layout.addWidget(device_label)
        self.status_indicator_label = QLabel("Állapot: Lekérdezés...")
        font_status = QFont("Arial", 11, QFont.Weight.Bold)
        self.status_indicator_label.setFont(font_status)
        top_left_layout.addWidget(self.status_indicator_label)
        top_bar_layout.addWidget(top_left_widget, 1)

        # Középső: Idő és Nap adatok
        info_widget = QWidget()
        info_layout = QVBoxLayout(info_widget)
        info_layout.setContentsMargins(0, 0, 0, 0)
        info_layout.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.time_label = QLabel("...")
        self.time_label.setFont(QFont("Arial", 13, QFont.Weight.Bold))
        self.time_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.time_label.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Preferred)
        info_layout.addWidget(self.time_label)
        try:
            sun_info = logic.get_local_sun_info()
            self.main_app.latitude = sun_info["latitude"]
            self.main_app.longitude = sun_info["longitude"]
            self.main_app.sunrise = sun_info["sunrise"]
            self.main_app.sunset = sun_info["sunset"]
            located = sun_info["located"]
        except Exception as e:
            log_event(f"Hiba a get_local_sun_info hívásakor GUI2 initben: {e}")
            located = False
            self.main_app.latitude = 47.4338
            self.main_app.longitude = 19.1931
            self.main_app.sunrise = None
            self.main_app.sunset = None
        sunrise_str = self.main_app.sunrise.strftime("%H:%M") if self.main_app.sunrise else "N/A"
        sunset_str = self.main_app.sunset.strftime("%H:%M") if self.main_app.sunset else "N/A"
        self.sun_label = QLabel(f"Napkelte: {sunrise_str} | Naplemente: {sunset_str}")
        self.sun_label.setFont(QFont("Arial", 11, QFont.Weight.Bold))
        self.sun_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        info_layout.addWidget(self.sun_label)
        lat = self.main_app.latitude
        lon = self.main_app.longitude
        tz_name = (
            logic.LOCAL_TZ.zone
            if hasattr(logic, "LOCAL_TZ") and hasattr(logic.LOCAL_TZ, "zone")
            else str(getattr(logic, "LOCAL_TZ", "Ismeretlen"))
        )
        self.coord_label = QLabel(f"Koordináták: {lat:.4f}°É, {lon:.4f}°K | Időzóna: {tz_name}")
        self.coord_label.setFont(QFont("Arial", 10, QFont.Weight.Bold))
        self.coord_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        info_layout.addWidget(self.coord_label)
        top_bar_layout.addWidget(info_widget, 2)

        # Jobb: Pozíció státusz
        top_right_widget = QWidget()
        top_right_layout = QVBoxLayout(top_right_widget)
        top_right_layout.setContentsMargins(0, 0, 0, 0)
        top_right_layout.setAlignment(Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignRight)
        status_text = "Pozíció: Meghatározva" if located else "Pozíció: Alapértelmezett"
        status_color = "lime" if located else "#FFA500"
        self.position_status_label = QLabel(status_text)
        font_pos_status = QFont("Arial", 10, QFont.Weight.Bold)
        self.position_status_label.setFont(font_pos_status)
        self.position_status_label.setStyleSheet(f"color: {status_color}; background-color: transparent;")
        top_right_layout.addWidget(self.position_status_label)
        coord_only_label = QLabel(f"({lat:.2f}, {lon:.2f})")
        coord_only_label.setFont(QFont("Arial", 8))
        coord_only_label.setStyleSheet("color: gray; background-color: transparent;")
        top_right_layout.addWidget(coord_only_label, 0, Qt.AlignmentFlag.AlignRight)
        top_bar_layout.addWidget(top_right_widget, 1)
        # --- Felső sáv vége ---
        main_layout.addLayout(top_bar_layout)
        # main_layout.addSpacing(120) # Korábbi nagy térköz visszaállítása
        # Kisebb távolság a felső sáv és a vezérlők között, hogy a profilválasztó
        # látható maradjon kisebb képernyőn is
        main_layout.addSpacing(10)

        # --- Vezérlő Widget ---
        self.controls_widget = GUI2_ControlsWidget(self.main_app)
        main_layout.addWidget(self.controls_widget, 0, Qt.AlignmentFlag.AlignCenter)
        main_layout.addSpacing(10)

        # Betöltjük az ütemezési profilokat még a profilválasztó létrehozása előtt
        logic.load_profiles_from_file(self.main_app)
        if not hasattr(self.main_app, "profiles") or not self.main_app.profiles:
            self.main_app.profiles = {"Alap": {"active": True, "schedule": logic.get_default_schedule()}}

        self.default_profile_name = list(self.main_app.profiles.keys())[0]
        self.current_profile_name = self.default_profile_name
        self.main_app.schedule = self.main_app.profiles[self.current_profile_name]["schedule"]
        self.unsaved_changes = False

        # --- Profilválasztó ---
        profile_container = QVBoxLayout()
        profile_layout = QHBoxLayout()
        profile_label = QLabel("Profil:")
        self.profile_combo = NoWheelComboBox()
        self.profile_combo.addItems(list(self.main_app.profiles.keys()))
        self.profile_combo.setCurrentText(self.current_profile_name)
        self.profile_combo.currentTextChanged.connect(self.change_profile)
        self.profile_active_checkbox = QCheckBox("Aktív")
        self.profile_active_checkbox.setChecked(self.main_app.profiles[self.current_profile_name].get("active", True))
        self.profile_active_checkbox.stateChanged.connect(self.toggle_profile_active)
        profile_layout.addWidget(profile_label)
        profile_layout.addWidget(self.profile_combo)
        profile_layout.addWidget(self.profile_active_checkbox)
        profile_layout.addStretch(1)
        profile_container.addLayout(profile_layout)

        add_profile_btn = QPushButton("Új profil")
        add_profile_btn.setObjectName("addProfileButton")
        add_profile_btn.setFixedSize(80, 25)
        add_profile_btn.clicked.connect(self.add_profile)

        custom_color_btn = QPushButton("Egyedi szín")
        custom_color_btn.setObjectName("customColorButton")
        custom_color_btn.setFixedSize(100, 25)
        custom_color_btn.clicked.connect(self.open_custom_colors)

        btn_layout = QHBoxLayout()
        btn_layout.addWidget(add_profile_btn)
        btn_layout.addWidget(custom_color_btn)
        btn_layout.addStretch(1)
        profile_container.addLayout(btn_layout)

        delete_profile_btn = QPushButton("Profil törlése")
        delete_profile_btn.setObjectName("deleteProfileButton")
        delete_profile_btn.setFixedSize(80, 25)
        delete_profile_btn.clicked.connect(self.delete_profile)
        profile_container.addWidget(delete_profile_btn, 0, Qt.AlignmentFlag.AlignLeft)

        main_layout.addLayout(profile_container)

        # Timeline visualization (shows all profiles)
        self.timeline_widget = TimelineWidget(self.main_app)
        main_layout.addSpacing(25)
        main_layout.addWidget(self.timeline_widget)
        main_layout.addSpacing(15)

        # --- Ütemező Táblázat (GroupBox nélkül) ---
        table_container = QWidget()
        table_layout = QGridLayout(table_container)
        table_layout.setSpacing(5)
        table_layout.setHorizontalSpacing(15)
        table_layout.setVerticalSpacing(8)
        table_layout.setColumnStretch(1, 1)
        table_layout.setColumnStretch(2, 0)
        table_layout.setColumnStretch(3, 0)
        table_layout.setColumnStretch(5, 0)
        table_layout.setColumnStretch(7, 0)
        headers = ["Nap", "Szín", "Fel", "Le", "Napkelte", "+/-", "Napnyugta", "+/-"]
        for i, header in enumerate(headers):
            label = QLabel(header)
            label.setFont(QFont("Arial", 10, QFont.Weight.Bold))
            align = Qt.AlignmentFlag.AlignLeft if i == 0 else Qt.AlignmentFlag.AlignCenter
            table_layout.addWidget(label, 0, i, align)
        self.schedule_widgets = {}
        self.time_comboboxes = []
        color_display_names = ["Nincs kiválasztva"] + [c[0] for c in COLORS]
        valid_color_names = [c[0] for c in COLORS]
        for i, day_hu in enumerate(DAYS):
            row = i + 1
            day_widgets = {}
            schedule_data = self.main_app.schedule.get(day_hu, {})
            day_label = QLabel(day_hu, font=QFont("Arial", 10))
            table_layout.addWidget(
                day_label,
                row,
                0,
                Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter,
            )
            color_cb = NoWheelComboBox()
            color_cb.addItems(color_display_names)
            saved_color = schedule_data.get("color", "")
            color_cb.setCurrentIndex(color_display_names.index(saved_color) if saved_color in valid_color_names else 0)
            color_cb.currentTextChanged.connect(self.mark_unsaved)
            table_layout.addWidget(color_cb, row, 1)
            day_widgets["color"] = color_cb
            time_values = [""] + [f"{h:02d}:{m:02d}" for h in range(24) for m in range(0, 60, 5)]
            on_time_cb = NoWheelComboBox()
            on_time_cb.addItems(time_values)
            on_time_cb.setEditable(True)
            on_time_cb.setCurrentText(schedule_data.get("on_time", ""))
            on_time_cb.setFixedWidth(70)
            on_time_cb.currentTextChanged.connect(self.mark_unsaved)
            table_layout.addWidget(on_time_cb, row, 2, Qt.AlignmentFlag.AlignCenter)
            day_widgets["on_time"] = on_time_cb
            self.time_comboboxes.append(on_time_cb)
            off_time_cb = NoWheelComboBox()
            off_time_cb.addItems(time_values)
            off_time_cb.setEditable(True)
            off_time_cb.setCurrentText(schedule_data.get("off_time", ""))
            off_time_cb.setFixedWidth(70)
            off_time_cb.currentTextChanged.connect(self.mark_unsaved)
            table_layout.addWidget(off_time_cb, row, 3, Qt.AlignmentFlag.AlignCenter)
            day_widgets["off_time"] = off_time_cb
            self.time_comboboxes.append(off_time_cb)
            sunrise_cb = QCheckBox()
            sunrise_cb.setChecked(schedule_data.get("sunrise", False))
            table_layout.addWidget(sunrise_cb, row, 4, Qt.AlignmentFlag.AlignCenter)
            day_widgets["sunrise"] = sunrise_cb
            sunrise_cb.stateChanged.connect(
                lambda state, idx=i * 2, d=day_hu: (
                    self.mark_unsaved(),
                    self.toggle_sun_time(state, idx, d, "sunrise"),
                )
            )
            sunrise_offset_entry = NoWheelLineEdit(str(schedule_data.get("sunrise_offset", 0)))
            sunrise_offset_entry.setFixedWidth(40)
            sunrise_offset_entry.setAlignment(Qt.AlignmentFlag.AlignCenter)
            sunrise_offset_entry.textChanged.connect(self.mark_unsaved)
            table_layout.addWidget(sunrise_offset_entry, row, 5, Qt.AlignmentFlag.AlignCenter)
            day_widgets["sunrise_offset"] = sunrise_offset_entry
            sunset_cb = QCheckBox()
            sunset_cb.setChecked(schedule_data.get("sunset", False))
            table_layout.addWidget(sunset_cb, row, 6, Qt.AlignmentFlag.AlignCenter)
            day_widgets["sunset"] = sunset_cb
            sunset_cb.stateChanged.connect(
                lambda state, idx=i * 2 + 1, d=day_hu: (
                    self.mark_unsaved(),
                    self.toggle_sun_time(state, idx, d, "sunset"),
                )
            )
            sunset_offset_entry = NoWheelLineEdit(str(schedule_data.get("sunset_offset", 0)))
            sunset_offset_entry.setFixedWidth(40)
            sunset_offset_entry.setAlignment(Qt.AlignmentFlag.AlignCenter)
            sunset_offset_entry.textChanged.connect(self.mark_unsaved)
            table_layout.addWidget(sunset_offset_entry, row, 7, Qt.AlignmentFlag.AlignCenter)
            day_widgets["sunset_offset"] = sunset_offset_entry
            self.schedule_widgets[day_hu] = day_widgets
            self.toggle_sun_time(sunrise_cb.checkState(), i * 2, day_hu, "sunrise")
            self.toggle_sun_time(sunset_cb.checkState(), i * 2 + 1, day_hu, "sunset")
        # --- Ütemező Táblázat Vége ---
        main_layout.addWidget(table_container, 0, Qt.AlignmentFlag.AlignCenter)  # Hozzáadás a fő layout-hoz
        main_layout.addSpacing(10)  # Kis térköz a táblázat és a gombok között

        # --- Ütemező és Indítási Gombok / Checkbox egy sorban ---
        schedule_action_layout = QHBoxLayout()
        schedule_action_layout.setContentsMargins(10, 0, 10, 0)  # Bal és jobb margó

        self.startup_checkbox = QCheckBox("Indítás a Windows-zal")
        # Hibaellenőrzés: Csak akkor próbáljuk beállítani, ha a config_manager nem a dummy
        if not isinstance(config_manager, type) or config_manager.__name__ != "DummyManager":
            try:
                self.startup_checkbox.setChecked(config_manager.get_setting("start_with_windows"))
                self.startup_checkbox.stateChanged.connect(self.toggle_startup)
            except Exception as e_cfg:
                log_event(f"Hiba a startup checkbox beállításakor: {e_cfg}")
                self.startup_checkbox.setEnabled(False)  # Letiltjuk, ha hiba történt
        else:
            log_event("ConfigManager dummy, startup checkbox letiltva.")
            self.startup_checkbox.setEnabled(False)
        schedule_action_layout.addWidget(self.startup_checkbox)  # Checkbox bal oldalon

        schedule_action_layout.addStretch(1)  # Térkitöltő középen

        # Mentés gomb felül, alatta egy sorban Alaphelyzet és Vissza
        save_back_layout = QVBoxLayout()
        save_back_layout.setAlignment(Qt.AlignmentFlag.AlignRight)

        save_button = QPushButton("Mentés")
        save_button.clicked.connect(self.save_profile_slot)
        save_back_layout.addWidget(save_button)

        reset_back_row = QHBoxLayout()
        reset_back_row.setAlignment(Qt.AlignmentFlag.AlignRight)

        reset_button = QPushButton("Alaphelyzet")
        reset_button.clicked.connect(self.reset_schedule_gui)
        reset_back_row.addWidget(reset_button)

        back_button = QPushButton("Vissza")
        try:
            back_button.clicked.connect(self.main_app.gui_manager.load_gui1)
        except AttributeError as e:
            log_event(f"HIBA a Vissza gomb connect során: {e}.")
            back_button.setEnabled(False)
        reset_back_row.addWidget(back_button)

        save_back_layout.addLayout(reset_back_row)

        schedule_action_layout.addLayout(save_back_layout)

        main_layout.addLayout(schedule_action_layout)  # Hozzáadás a fő layout-hoz
        # --- Ütemező és Indítási Gombok Vége ---

        main_layout.addStretch(1)  # Rugalmas térköz alul

        # --- Időzítők ---
        self.update_time_timer = QTimer(self)
        self.check_schedule_timer = QTimer(self)
        self.update_time_timer.timeout.connect(self.update_time)
        self.update_time_timer.start(1000)
        self.check_schedule_timer.timeout.connect(lambda: logic.apply_color_from_visualization(self))
        self.check_schedule_timer.start(5000)
        self.update_time()
        QTimer.singleShot(500, lambda: logic.apply_color_from_visualization(self))

    # --- Slot Metódusok (változatlanok) ---
    def stop_timers(self):
        """Leállítja az időzítőket."""
        log_event("GUI2 Timers stopping...")
        if hasattr(self, "update_time_timer"):
            self.update_time_timer.stop()
        if hasattr(self, "check_schedule_timer"):
            self.check_schedule_timer.stop()
        if hasattr(self, "timeline_widget") and hasattr(self.timeline_widget, "timer"):
            self.timeline_widget.timer.stop()
        log_event("GUI2 Timers stopped.")

    @Slot()
    def mark_unsaved(self):
        """Jelzi, hogy módosítás történt a jelenlegi profilon."""
        self.unsaved_changes = self.is_schedule_modified()
        if hasattr(self, "timeline_widget"):
            self.timeline_widget.refresh()

    @Slot()
    def save_profile_slot(self):
        """Saves profile then refreshes the timeline widget."""
        logic.save_profile(self)
        if hasattr(self, "timeline_widget"):
            self.timeline_widget.refresh()

    def is_schedule_modified(self):
        """Összehasonlítja a jelenlegi beállításokat a mentett profillal."""
        current = {}
        for day, widgets in self.schedule_widgets.items():
            try:
                sr_off = int(widgets["sunrise_offset"].text()) if widgets["sunrise_offset"].text() else 0
            except ValueError:
                sr_off = 0
            try:
                ss_off = int(widgets["sunset_offset"].text()) if widgets["sunset_offset"].text() else 0
            except ValueError:
                ss_off = 0
            current[day] = {
                "color": widgets["color"].currentText(),
                "on_time": widgets["on_time"].currentText(),
                "off_time": widgets["off_time"].currentText(),
                "sunrise": widgets["sunrise"].isChecked(),
                "sunrise_offset": sr_off,
                "sunset": widgets["sunset"].isChecked(),
                "sunset_offset": ss_off,
            }
        original = self.main_app.profiles.get(self.current_profile_name, {}).get("schedule", {})
        return current != original

    @Slot(str)
    def change_profile(self, name: str):
        """Profil váltása a lenyitható menüből."""
        if not name or name not in self.main_app.profiles:
            return
        if name == self.current_profile_name:
            return
        if self.unsaved_changes:
            mb = QMessageBox(self)
            mb.setWindowTitle("Mentés")
            mb.setText("A módosítások nincsenek elmentve. Szeretnéd elmenteni a profil váltása előtt?")
            yes_btn = mb.addButton("Igen", QMessageBox.ButtonRole.YesRole)
            no_btn = mb.addButton("Nem", QMessageBox.ButtonRole.NoRole)
            cancel_btn = mb.addButton("Mégsem", QMessageBox.ButtonRole.RejectRole)
            for b in (yes_btn, no_btn, cancel_btn):
                b.setFixedWidth(int(b.sizeHint().width() * 0.5))
            mb.setDefaultButton(cancel_btn)
            mb.exec()
            clicked = mb.clickedButton()
            if clicked == yes_btn:
                logic.save_profile(self)
            elif clicked == cancel_btn:
                self.profile_combo.blockSignals(True)
                self.profile_combo.setCurrentText(self.current_profile_name)
                self.profile_combo.blockSignals(False)
                return
        self.current_profile_name = name
        self.main_app.schedule = self.main_app.profiles[name]["schedule"]
        self.unsaved_changes = False
        for day, widgets in self.schedule_widgets.items():
            data = self.main_app.schedule.get(day, {})
            widgets["color"].setCurrentText(data.get("color", ""))
            widgets["on_time"].setCurrentText(data.get("on_time", ""))
            widgets["off_time"].setCurrentText(data.get("off_time", ""))
            widgets["sunrise"].setChecked(data.get("sunrise", False))
            widgets["sunrise_offset"].setText(str(data.get("sunrise_offset", 0)))
            widgets["sunset"].setChecked(data.get("sunset", False))
            widgets["sunset_offset"].setText(str(data.get("sunset_offset", 0)))
            self.toggle_sun_time(
                widgets["sunrise"].checkState(),
                list(DAYS).index(day) * 2,
                day,
                "sunrise",
            )
            self.toggle_sun_time(
                widgets["sunset"].checkState(),
                list(DAYS).index(day) * 2 + 1,
                day,
                "sunset",
            )
        self.profile_active_checkbox.blockSignals(True)
        self.profile_active_checkbox.setChecked(self.main_app.profiles[name].get("active", True))
        self.profile_active_checkbox.blockSignals(False)
        if hasattr(self, "timeline_widget"):
            self.timeline_widget.refresh()

    @Slot()
    def add_profile(self):
        """Új profil létrehozása."""
        name, ok = QInputDialog.getText(self, "Új profil", "Profil neve:")
        if not ok or not name:
            return
        if name in self.main_app.profiles:
            QMessageBox.warning(self, "Hiba", "Már létezik ilyen profil.")
            return
        self.main_app.profiles[name] = {
            "active": False,
            "schedule": logic.get_default_schedule(),
        }
        self.profile_combo.addItem(name)
        self.profile_combo.setCurrentText(name)
        logic._save_profiles_to_file(self.main_app)

    @Slot()
    def delete_profile(self):
        """Törli a kiválasztott profilt, ha nem az alapértelmezett."""
        if self.current_profile_name == getattr(self, "default_profile_name", ""):
            QMessageBox.information(self, "Hiba", "Az alapértelmezett profil nem törölhető.")
            return
        reply = QMessageBox.question(
            self,
            "Profil törlése",
            f"Biztosan törlöd a(z) '{self.current_profile_name}' profilt?",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if reply != QMessageBox.StandardButton.Yes:
            return
        del self.main_app.profiles[self.current_profile_name]
        idx = self.profile_combo.currentIndex()
        self.profile_combo.removeItem(idx)
        logic._save_profiles_to_file(self.main_app)
        self.unsaved_changes = False
        new_name = self.profile_combo.currentText()
        if new_name:
            self.change_profile(new_name)

    @Slot(int)
    def toggle_profile_active(self, state):
        """Aktív jelölő változása."""
        active = state == Qt.CheckState.Checked.value
        self.main_app.profiles[self.current_profile_name]["active"] = active
        if active:
            conflicts = logic.check_profile_conflicts(self.main_app, self.current_profile_name)
            if conflicts:
                QMessageBox.warning(
                    self,
                    "Ütközés",
                    "A(z) {} profil ütközik az alábbiakkal:\n{}".format(
                        self.current_profile_name, "\n".join(conflicts)
                    ),
                )
                self.main_app.profiles[self.current_profile_name]["active"] = False
                self.profile_active_checkbox.blockSignals(True)
                self.profile_active_checkbox.setChecked(False)
                self.profile_active_checkbox.blockSignals(False)
                return
        logic._save_profiles_to_file(self.main_app)

    @Slot()
    def reset_schedule_gui(self):
        """Visszaállítja az ütemező GUI-t az alapértelmezett értékekre."""
        reply = QMessageBox.question(
            self,
            "Alaphelyzet",
            "Biztosan visszaállítod az összes ütemezési beállítást az alapértelmezettre?\n(Ez a művelet nem menti a változásokat.)",
            QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
            QMessageBox.StandardButton.No,
        )
        if reply == QMessageBox.StandardButton.Yes:
            log_event("Ütemező GUI visszaállítása alaphelyzetbe...")
            for day, widgets in self.schedule_widgets.items():
                widgets["color"].setCurrentIndex(0)
                widgets["on_time"].setCurrentText("")
                widgets["off_time"].setCurrentText("")
                widgets["sunrise"].setChecked(False)
                widgets["sunrise_offset"].setText("0")
                widgets["sunset"].setChecked(False)
                widgets["sunset_offset"].setText("0")
                self.toggle_sun_time(
                    Qt.CheckState.Unchecked.value,
                    list(DAYS).index(day) * 2,
                    day,
                    "sunrise",
                )
                self.toggle_sun_time(
                    Qt.CheckState.Unchecked.value,
                    list(DAYS).index(day) * 2 + 1,
                    day,
                    "sunset",
                )
            self.unsaved_changes = self.is_schedule_modified()

    @Slot(int)
    def toggle_startup(self, state):
        """Kezeli a "Start with Windows" checkbox állapotváltozását."""
        is_checked = bool(state == Qt.CheckState.Checked.value)
        log_event(f"'Indítás a Windows-zal' checkbox {'bekapcsolva' if is_checked else 'kikapcsolva'}.")

        is_dummy_cfg = isinstance(config_manager, type) and config_manager.__name__ == "DummyManager"
        is_dummy_reg = isinstance(registry_utils, type) and registry_utils.__name__ == "DummyManager"
        if is_dummy_cfg or is_dummy_reg:
            log_event(
                "HIBA: Nem lehet módosítani az indítási beállításokat, mert a config/registry manager nem töltődött be helyesen."
            )
            QMessageBox.critical(
                self,
                "Import Hiba",
                "Nem sikerült betölteni a beállításkezelő modulokat. Az indítási beállítás nem módosítható.",
            )
            self.startup_checkbox.blockSignals(True)
            self.startup_checkbox.setChecked(not is_checked)
            self.startup_checkbox.blockSignals(False)
            return

        config_manager.set_setting("start_with_windows", is_checked)
        success = False
        if is_checked:
            success = registry_utils.add_to_startup()
            if not success:
                QMessageBox.warning(
                    self,
                    "Hiba",
                    "Nem sikerült hozzáadni az alkalmazást az indítópulthoz.\nLehet, hogy nincs megfelelő jogosultság.",
                )
        else:
            success = registry_utils.remove_from_startup()
            if not success:
                QMessageBox.warning(
                    self,
                    "Hiba",
                    "Nem sikerült eltávolítani az alkalmazást az indítópultból.\nLehet, hogy nincs megfelelő jogosultság.",
                )

        if not success:
            log_event("Registry művelet sikertelen, checkbox és beállítás visszaállítása.")
            self.startup_checkbox.blockSignals(True)
            self.startup_checkbox.setChecked(not is_checked)
            self.startup_checkbox.blockSignals(False)
            config_manager.set_setting("start_with_windows", not is_checked)

    @Slot(int, int, str, str)
    def toggle_sun_time(self, state, list_index, day, sun_event_type):
        """Engedélyezi/letiltja a megfelelő idő és offset mezőket."""
        is_checked = bool(state == Qt.CheckState.Checked.value)
        offset_entry = self.schedule_widgets[day][f"{sun_event_type}_offset"]
        time_combo_key = "on_time" if sun_event_type == "sunrise" else "off_time"
        time_combo = self.schedule_widgets[day][time_combo_key]

        if is_checked:
            time_combo.setEnabled(False)
            time_combo.setStyleSheet("color: gray; background-color: lightgray;")
            offset_entry.setEnabled(True)
        else:
            time_combo.setEnabled(True)
            time_combo.setStyleSheet("")
            offset_entry.setEnabled(False)

    @Slot()
    def update_time(self):
        """Frissíti a GUI-n megjelenő időt."""
        try:
            # Használjuk a logic modulban definiált időzónát
            now = datetime.now(logic.LOCAL_TZ)
            magyar_nap = DAYS_HU.get(now.strftime("%A"), now.strftime("%A"))
            self.time_label.setText(f"{now.strftime('%Y.%m.%d')} | {magyar_nap} | {now.strftime('%H:%M:%S')}")
        except Exception as e:
            log_event(f"Hiba az idő frissítésekor: {e}")
            self.time_label.setText("Idő hiba")

    @Slot()
    def open_custom_colors(self):
        dialog = CustomColorDialog(self)
        dialog.exec()
        self.controls_widget.build_color_buttons()
        self.refresh_color_inputs()

    def refresh_color_inputs(self):
        names = ["Nincs kiválasztva"] + [c[0] for c in COLORS]
        for widgets in self.schedule_widgets.values():
            cb = widgets["color"]
            current = cb.currentText()
            cb.blockSignals(True)
            cb.clear()
            cb.addItems(names)
            if current in names:
                cb.setCurrentText(current)
            else:
                cb.setCurrentIndex(0)
            cb.blockSignals(False)
