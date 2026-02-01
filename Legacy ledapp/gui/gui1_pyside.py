# LEDapp/gui/gui1_pyside.py (Objektumnévvel)

from PySide6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QListWidget,
    QProgressBar,
    QMessageBox,
    QSizePolicy,
    QAbstractItemView,
    QSpacerItem,
)
from PySide6.QtCore import Qt, Slot
from PySide6.QtGui import QFont

# Logolás importálása
try:
    from core.reconnect_handler import log_event
except ImportError:

    def log_event(msg):
        print(f"[LOG - Dummy GUI1]: {msg}")


class GUI1_Widget(QWidget):
    def __init__(self, main_app, parent=None):
        super().__init__(parent)
        self.setObjectName("GUI1_Widget_Instance")  # <<< OBJEKTUMNÉV HOZZÁADVA >>>
        self.main_app = main_app

        # --- Layout és Widgetek ---
        # ... (A többi kód változatlan ebben a fájlban a legutóbbi javítás óta) ...
        # Fő layout
        layout = QVBoxLayout(self)
        layout.setContentsMargins(20, 20, 20, 20)
        layout.setSpacing(10)

        # Cím
        title_label = QLabel("LED ESZKÖZ KERESÉSE")
        font = QFont("Arial", 16, QFont.Weight.Bold)
        title_label.setFont(font)
        title_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        layout.addWidget(title_label)

        # Progress Bar Frame
        progress_frame_layout = QHBoxLayout()
        progress_widget = QWidget()
        progress_widget.setLayout(progress_frame_layout)
        progress_frame_layout.setContentsMargins(0, 0, 0, 0)
        progress_frame_layout.setSpacing(5)
        self.progress_label = QLabel("")
        font_small = QFont("Arial", 10)
        self.progress_label.setFont(font_small)
        self.progress_label.setStyleSheet("color: gray;")
        self.progress_label.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Fixed)
        progress_frame_layout.addWidget(self.progress_label, 0)
        self.progress_bar = QProgressBar()
        self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(0)
        self.progress_bar.setTextVisible(False)
        self.progress_bar.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
        progress_frame_layout.addWidget(self.progress_bar, 1)
        layout.addWidget(progress_widget)

        # Eszközlista
        self.device_listbox = QListWidget()
        font_list = QFont("Arial", 12)
        self.device_listbox.setFont(font_list)
        self.device_listbox.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.device_listbox.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Expanding)
        layout.addWidget(self.device_listbox)
        self.device_listbox.itemDoubleClicked.connect(self.on_device_double_click)

        # Gombok Frame
        button_frame_layout = QHBoxLayout()
        button_widget = QWidget()
        button_widget.setLayout(button_frame_layout)
        button_frame_layout.setContentsMargins(0, 0, 0, 0)
        button_frame_layout.setSpacing(5)
        button_frame_layout.addSpacerItem(QSpacerItem(40, 20, QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Minimum))
        self.search_button = QPushButton("Keresés")
        self.search_button.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Fixed)
        self.search_button.clicked.connect(self.search_devices)
        button_frame_layout.addWidget(self.search_button)
        self.connect_button = QPushButton("Csatlakozás")
        self.connect_button.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Fixed)
        self.connect_button.clicked.connect(self.connect_device)
        button_frame_layout.addWidget(self.connect_button)
        self.disconnect_button = QPushButton("Kapcsolat bontása")
        self.disconnect_button.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Fixed)
        self.disconnect_button.clicked.connect(self.main_app.disconnect_device)
        button_frame_layout.addWidget(self.disconnect_button)
        self.goto_gui2_button = QPushButton("→")
        self.goto_gui2_button.setSizePolicy(QSizePolicy.Policy.Fixed, QSizePolicy.Policy.Fixed)
        self.goto_gui2_button.clicked.connect(self.main_app.gui_manager.load_gui2)  # GuiManageren keresztül
        button_frame_layout.addWidget(self.goto_gui2_button)
        button_frame_layout.addSpacerItem(QSpacerItem(40, 20, QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Minimum))
        layout.addWidget(button_widget)

        self.update_button_states()
        if self.main_app.devices:
            self.update_device_list()

    # ... (on_device_double_click, update_device_list, update_button_states változatlan) ...
    @Slot()
    def on_device_double_click(self):
        if self.connect_button.isEnabled() and self.connect_button.isVisible():
            self.connect_device()

    def update_device_list(self):
        self.device_listbox.clear()
        for name, addr in self.main_app.devices:
            self.device_listbox.addItem(f"{name} ({addr})")

    def update_button_states(self):
        connected = self.main_app.connected
        has_devices = bool(self.main_app.devices)
        is_scanning_or_connecting = self.progress_bar.minimum() == 0 and self.progress_bar.maximum() == 0

        self.connect_button.setVisible(not connected)
        self.disconnect_button.setVisible(connected)
        self.goto_gui2_button.setVisible(connected)

        self.search_button.setEnabled(not is_scanning_or_connecting)
        self.connect_button.setEnabled(has_devices and not connected and not is_scanning_or_connecting)
        self.disconnect_button.setEnabled(connected and not is_scanning_or_connecting)
        self.goto_gui2_button.setEnabled(connected and not is_scanning_or_connecting)

    # ... (search_devices, on_scan_finished, on_scan_error, on_scan_finally változatlan, a run_async_task hívás már a signalokat használja) ...
    @Slot()
    def search_devices(self):
        self.progress_label.setText("Keresés folyamatban...")
        self.progress_bar.setRange(0, 0)
        self.update_button_states()
        self.main_app.async_helper.run_async_task(
            self.main_app.ble.scan(),
            self.main_app.scan_results_signal,
            self.main_app.scan_error_signal,
        )

    @Slot(object)
    def on_scan_finished(self, devices):
        self.main_app.devices = devices
        self.update_device_list()
        self.progress_label.setText(f"{len(devices)} eszköz található")

    @Slot(str)
    def on_scan_error(self, error_message):
        # Avoid resizing the main window by keeping this label short
        # The detailed error is shown in the message box instead
        self.progress_label.setText("Hiba a keresés során.")
        QMessageBox.critical(self, "Keresési Hiba", f"Hiba a keresés során:\n{error_message}")

    @Slot()
    def on_scan_finally(self):
        self.progress_bar.setRange(0, 100)
        self.progress_bar.setValue(100)
        self.update_button_states()

    # ... (connect_device, on_connect_finished, on_connect_error, on_connect_finally változatlan, a run_async_task hívás már a signalokat használja) ...
    @Slot()
    def connect_device(self):
        selected_items = self.device_listbox.selectedItems()
        if not selected_items:
            if not self.main_app.devices:
                QMessageBox.information(self, "Nincs Eszköz", "Először keress eszközöket.")
                return
            QMessageBox.warning(self, "Nincs kiválasztva", "Kérlek válassz ki egy eszközt a listából.")
            return

        current_row = self.device_listbox.currentRow()
        if current_row < 0 or current_row >= len(self.main_app.devices):
            QMessageBox.critical(self, "Hiba", "Érvénytelen kiválasztás.")
            return

        name, address = self.main_app.devices[current_row]
        self.main_app.selected_device = (name, address)

        self.progress_label.setText(f"Csatlakozás: {name}...")
        self.progress_bar.setRange(0, 0)
        self.update_button_states()

        self.main_app.async_helper.run_async_task(
            self.main_app.ble.connect_with_retry(address),
            self.main_app.connect_results_signal,
            self.main_app.connect_error_signal,
        )

    @Slot(bool)
    def on_connect_finished(self, success):
        if not success:
            self.progress_label.setText("Sikertelen csatlakozás.")
            self.main_app.selected_device = None

    @Slot(str)
    def on_connect_error(self, error_message):
        self.progress_label.setText(f"Csatlakozási hiba.")
        # A QMessageBox-ot a _handle_connect_error kezeli
        self.main_app.selected_device = None

    @Slot()
    def on_connect_finally(self):
        if not self.main_app.connected:
            self.progress_bar.setRange(0, 100)
            self.progress_bar.setValue(100)
            # A progress labelt hagyjuk, hogy mutassa a hibát vagy az eredményszámot (scan után)
            # self.progress_label.setText("") # Ezt kivesszük
            self.update_button_states()
