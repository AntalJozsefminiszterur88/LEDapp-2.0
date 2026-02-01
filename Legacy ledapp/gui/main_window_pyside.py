# LEDapp/gui/main_window_pyside.py (Kiegészítve)

import os
import sys  # Hozzáadva sys import

from PySide6.QtWidgets import QApplication, QMessageBox, QSystemTrayIcon, QMenu
from PySide6.QtCore import (
    Qt,
    Slot,
    QTimer,
    QMetaObject,
    Q_ARG,
)  # QMetaObject és Q_ARG hozzáadva
from PySide6.QtGui import QIcon, QAction

# Importáljuk az alap ablak osztályt és a GUI managert
try:
    # Próbáljuk meg relatívan importálni
    from .main_window_base import LEDApp_BaseWindow, log_event
    from .gui_manager import GuiManager  # GuiManager importálása

    # GUI widgetek importálása az isinstance és egyéb hivatkozások miatt
except ImportError:
    # Ha nem a 'gui' mappából futtatjuk
    from main_window_base import LEDApp_BaseWindow, log_event


class LEDApp_PySide(LEDApp_BaseWindow):
    def __init__(self, start_hidden=False, parent=None):  # start_hidden paraméter hozzáadva
        super().__init__(parent)
        self._force_quit = False
        self._start_hidden = start_hidden  # Indítási állapot tárolása
        self._initial_gui_loaded = False  # Segédfalg, hogy tudjuk, betöltöttük-e már a GUI-t

        # ----- Rendszer Tálca Ikon Létrehozása -----
        self.tray_icon = None
        # Ikon betöltési logika (exe mellől is próbálkozik)
        app_icon = QApplication.instance().windowIcon() if QApplication.instance() else QIcon()
        if app_icon.isNull():
            icon_path = "led_icon.ico"
            if os.path.exists(icon_path):
                app_icon = QIcon(icon_path)
                log_event(f"Alkalmazás ikont '{icon_path}'-ból töltöttük be.")
            elif getattr(sys, "frozen", False):
                base_path = getattr(sys, "_MEIPASS", os.path.dirname(sys.executable))
                frozen_icon_path = os.path.join(base_path, icon_path)
                if os.path.exists(frozen_icon_path):
                    app_icon = QIcon(frozen_icon_path)
                    log_event(f"Alkalmazás ikont a fagyasztott helyről '{frozen_icon_path}'-ból töltöttük be.")
                else:
                    log_event(
                        f"Figyelmeztetés: Az ikonfájl ('{icon_path}') nem található sem relatívan, sem a fagyasztott helyen."
                    )
            else:
                log_event(f"Figyelmeztetés: Az ikonfájl ('{icon_path}') nem található.")

        if not app_icon.isNull():
            self.tray_icon = QSystemTrayIcon(app_icon, self)
            # Kezdeti tooltip beállítása (ezt az update_connection_status_gui frissíti)
            self.tray_icon.setToolTip("LED-Irányító 2000 (Inicializálás...)")

            tray_menu = QMenu(self)
            show_action = QAction("Megjelenítés", self)
            exit_action = QAction("Kilépés", self)

            show_action.triggered.connect(self.show_window_from_tray)
            exit_action.triggered.connect(self.quit_application)

            tray_menu.addAction(show_action)
            tray_menu.addSeparator()
            tray_menu.addAction(exit_action)

            self.tray_icon.setContextMenu(tray_menu)
            self.tray_icon.activated.connect(self.handle_tray_activation)
            log_event("Rendszer tálca ikon sikeresen létrehozva.")
            # Ha rejtve kell indulni, akkor most jelenítsük meg a tálca ikont
            if self._start_hidden:
                self.tray_icon.show()
                log_event("Alkalmazás rejtve indul, tálca ikon megjelenítve.")
        else:
            log_event("Figyelmeztetés: Tálca ikon nem hozható létre, ikon nem elérhető.")
            if self._start_hidden:
                log_event("HIBA: Rejtett indítás kérése tálca ikon nélkül nem lehetséges!")
                self._start_hidden = False  # Nem tud rejtve indulni ikon nélkül
        # ----- Rendszer Tálca Ikon Vége -----

        # GUI betöltésének késleltetése, hogy az AsyncHelper elindulhasson
        if not self._start_hidden:
            # Normál esetben várjunk egy kicsit
            QTimer.singleShot(100, self.load_initial_gui)
        else:
            # Ha rejtve indul, nem töltjük be a GUI-t, csak ha a felhasználó kéri
            log_event("Rejtett indítás: GUI betöltése késleltetve.")
            # Az automatikus csatlakozást a main.py indítja el
            # Frissítsük a tooltipet az induláskor ismert állapottal
            self.update_connection_status_gui(self.connection_status)

    def load_initial_gui(self):
        """Betölti a kezdeti GUI-t (GUI1 vagy GUI2 a kapcsolat állapota szerint)."""
        if self._initial_gui_loaded:
            return  # Ne töltse be újra, ha már megtörtént

        log_event("Kezdeti GUI betöltése...")
        # A selected_device-ot is ellenőrizzük, mert lehet, hogy a kapcsolat 'connected',
        # de még nincs eszköz kiválasztva (pl. ha a config fájl sérült volt)
        # És ellenőrizzük, hogy a kezdeti csatlakozási kísérlet már lefutott-e (_initial_connection_attempted)
        if self.connected and self.selected_device and self._initial_connection_attempted:
            log_event("Kapcsolódva van és van kiválasztott eszköz, GUI2 betöltése...")
            self.gui_manager.load_gui2()
        # Ha még nem futott le a kezdeti csatlakozási kísérlet (pl. auto-connect még folyamatban),
        # akkor várjunk, és ne töltsük be még a GUI1-et sem, ha rejtve indultunk.
        elif not self._initial_connection_attempted and self._is_auto_starting:
            log_event("Kezdeti GUI betöltése felfüggesztve, amíg az auto-connect befejeződik.")
            # Időzítő, hogy később újra próbálkozzon, ha valamiért elakadna
            QTimer.singleShot(1000, self.load_initial_gui)
            return  # Ne csináljunk most semmit
        else:  # Ha nincs kapcsolat, vagy nincs eszköz, vagy a kezdeti próba sikertelen volt
            log_event("Nincs kapcsolat, vagy nincs eszköz, vagy auto-connect sikertelen. GUI1 betöltése...")
            # Biztosítjuk, hogy disconnected legyen a státusz
            self.connected = False
            self.update_connection_status_gui("disconnected")
            self.gui_manager.load_gui1()

        self.gui_manager.center_window()
        self._initial_gui_loaded = True

    @Slot()
    def show_window_from_tray(self):
        """Megjeleníti az ablakot a tálcáról és elrejti az ikont."""
        if self.tray_icon:
            self.tray_icon.hide()  # Elrejtjük a tálca ikont, amikor az ablak megjelenik
            log_event("Tálca ikon elrejtve (show_window_from_tray).")

        # Ha a GUI még nem volt betöltve (mert rejtve indult)
        if not self._initial_gui_loaded:
            self.load_initial_gui()  # Betölti a megfelelő GUI-t az aktuális állapot alapján

        self.showNormal()
        self.raise_()
        self.activateWindow()
        log_event("Főablak megjelenítve a tálcáról.")

    @Slot(QSystemTrayIcon.ActivationReason)
    def handle_tray_activation(self, reason):
        """Kezeli a tálca ikonra kattintást."""
        # Bal klikk (Trigger) vagy dupla klikk (DoubleClick) esetén
        if reason == QSystemTrayIcon.ActivationReason.Trigger or reason == QSystemTrayIcon.ActivationReason.DoubleClick:
            if self.isHidden():
                self.show_window_from_tray()
            else:
                # Ha látható az ablak, hozzuk előtérbe
                self.showNormal()  # Biztosítjuk, hogy ne legyen minimalizálva
                self.raise_()
                self.activateWindow()
        # Jobb klikk (Context) esetén a menü automatikusan megjelenik

    @Slot()
    def quit_application(self):
        """Biztonságosan bezárja az alkalmazást a tálcáról."""
        log_event("Kilépés kezdeményezve a tálca menüből.")
        self._force_quit = True
        self.close()  # Ez hívja meg a closeEvent-et, ami gondoskodik a cleanup-ról

    def _perform_cleanup(self):
        """Végrehajtja az alap cleanup-ot és elrejti a tálca ikont."""
        # Először hívjuk az ős cleanup metódusát
        log_event("Leszármazott _perform_cleanup hívása...")
        super().base_cleanup()
        # Utána elrejtjük a tálca ikont, ha van
        if self.tray_icon:
            self.tray_icon.hide()
            log_event("Tálca ikon elrejtve (cleanup).")
        log_event("Teljes cleanup befejezve.")

    def closeEvent(self, event):
        """Ablak bezárásakor rákérdezés vagy tálcára helyezés."""
        force_quit = getattr(self, "_force_quit", False)
        self._force_quit = False  # Flag azonnali visszaállítása

        if force_quit:
            log_event("Force quit flag észlelve, cleanup és kilépés...")
            self._perform_cleanup()  # Cleanup hívása
            event.accept()  # Bezárás elfogadása
            QApplication.quit()  # Teljes alkalmazás bezárása
            return

        # Ha van tálca ikon, akkor alapértelmezetten tálcára helyezünk
        if self.tray_icon:
            # Ha az ablak látható (nem a tálcán van)
            if not self.isHidden():
                log_event("Ablak bezárása, háttérbe helyezés a tálcára.")
                self.hide()
                self.tray_icon.setVisible(True)  # Biztosítjuk, hogy látható legyen
                self.tray_icon.show()
                # Üzenet megjelenítése csak akkor, ha nem épp most indult rejtve
                if not self._start_hidden:
                    self.tray_icon.showMessage(
                        "LED-Irányító 2000",
                        "Az alkalmazás a háttérben fut. Kattints ide a megnyitáshoz.",
                        QSystemTrayIcon.MessageIcon.Information,
                        3000,
                    )
                event.ignore()  # Megakadályozzuk az ablak tényleges bezárását
            else:
                # Ha már rejtve van (pl. start_hidden), a closeEvent nem hívódik meg,
                # de ha valahogy mégis, akkor ne csináljunk semmit
                event.ignore()
        else:
            # Ha nincs tálca ikon, a normál bezárás történik (rákérdezéssel)
            reply = QMessageBox.question(
                self,
                "Kilépés",
                "Biztosan ki akarsz lépni?",
                QMessageBox.StandardButton.Yes | QMessageBox.StandardButton.No,
                QMessageBox.StandardButton.No,
            )
            if reply == QMessageBox.StandardButton.Yes:
                log_event("Nincs tálca ikon, felhasználó a kilépést választotta.")
                self._perform_cleanup()  # Biztosítjuk a cleanupot itt is
                event.accept()
                QApplication.quit()
            else:
                log_event("Nincs tálca ikon, felhasználó a 'Mégsem'-et választotta.")
                event.ignore()
