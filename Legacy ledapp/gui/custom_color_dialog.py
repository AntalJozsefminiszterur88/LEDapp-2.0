from PySide6.QtWidgets import (
    QDialog,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QListWidget,
    QListWidgetItem,
    QColorDialog,
    QLineEdit,
)
from PySide6.QtGui import QColor

from config import CUSTOM_COLORS
from core.custom_color_manager import add_custom_color, delete_custom_color


class CustomColorDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Egyedi színek")
        self.selected_hex = "#ffffff"

        layout = QVBoxLayout(self)

        form_layout = QHBoxLayout()
        form_layout.addWidget(QLabel("Név:"))
        self.name_edit = QLineEdit()
        form_layout.addWidget(self.name_edit)
        self.pick_btn = QPushButton("Szín kiválasztása")
        self.pick_btn.clicked.connect(self.pick_color)
        form_layout.addWidget(self.pick_btn)
        layout.addLayout(form_layout)

        self.add_btn = QPushButton("Hozzáadás")
        self.add_btn.clicked.connect(self.add_color)
        layout.addWidget(self.add_btn)

        layout.addWidget(QLabel("Mentett színek:"))
        self.list_widget = QListWidget()
        layout.addWidget(self.list_widget)

        delete_btn = QPushButton("Törlés")
        delete_btn.clicked.connect(self.delete_selected)
        layout.addWidget(delete_btn)

        close_btn = QPushButton("Bezárás")
        close_btn.clicked.connect(self.accept)
        layout.addWidget(close_btn)

        self.refresh_list()

    def pick_color(self):
        color = QColorDialog.getColor(parent=self)
        if color.isValid():
            self.selected_hex = color.name()

    def add_color(self):
        name = self.name_edit.text().strip()
        if not name:
            return
        add_custom_color(name, self.selected_hex)
        self.name_edit.clear()
        self.refresh_list()

    def delete_selected(self):
        item = self.list_widget.currentItem()
        if item:
            delete_custom_color(item.text())
            self.refresh_list()

    def refresh_list(self):
        self.list_widget.clear()
        for c in CUSTOM_COLORS:
            item = QListWidgetItem(c["name"])
            color = QColor(c["hex"])
            item.setBackground(color)
            self.list_widget.addItem(item)
        # update COLORS combos may have changed; no direct call here
