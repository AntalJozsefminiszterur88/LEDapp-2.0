from PySide6.QtWidgets import QWidget
from PySide6.QtCore import QTimer, QRectF
from PySide6.QtGui import QColor, QPainter, QPen

from datetime import datetime

from config import DAYS
from gui import gui2_schedule_logic as logic


class TimelineWidget(QWidget):
    """Timeline visualization showing schedules from all profiles."""

    def __init__(self, main_app, parent=None):
        super().__init__(parent)
        self.main_app = main_app
        self.intervals = {}
        self.setMinimumHeight(160)

        self.timer = QTimer(self)
        self.timer.timeout.connect(self.update)
        self.timer.start(60_000)  # refresh every minute

        self.refresh()

    def refresh(self):
        """Reload intervals for all profiles and repaint."""
        self.intervals = logic.get_all_profiles_day_intervals(self.main_app)
        self.update()

    def paintEvent(self, event):
        painter = QPainter(self)
        left_margin = 60
        top_margin = 5
        bottom_margin = 20
        row_height = max(20, (self.height() - top_margin - bottom_margin) // len(DAYS))
        width = self.width() - left_margin - 10

        painter.fillRect(self.rect(), self.palette().window())
        painter.setPen(QPen(QColor("gray")))

        # draw hour grid
        for h in range(25):
            x = left_margin + width * h / 24
            painter.drawLine(int(x), top_margin, int(x), top_margin + row_height * len(DAYS))
            if h % 2 == 0:
                painter.drawText(int(x) - 10, top_margin + row_height * len(DAYS) + 15, f"{h:02d}")
        for i, day in enumerate(DAYS):
            y = top_margin + i * row_height
            painter.drawText(5, y + row_height * 0.7, day)
            painter.drawLine(left_margin, y, left_margin + width, y)
            for start, end, color in self.intervals.get(day, []):
                x1 = left_margin + width * start / (24 * 60)
                x2 = left_margin + width * end / (24 * 60)
                rect = QRectF(x1, y + 2, x2 - x1, row_height - 4)
                painter.fillRect(rect, QColor(color))

        # current time indicator
        now = datetime.now(logic.LOCAL_TZ)
        total_min = now.hour * 60 + now.minute
        x = left_margin + width * total_min / (24 * 60)
        pen = QPen(QColor("red"))
        pen.setWidth(2)
        painter.setPen(pen)
        painter.drawLine(int(x), top_margin, int(x), top_margin + row_height * len(DAYS))
