# LEDapp/core/config_manager.py

import json
import os
import traceback

from config import BASE_DIR

# Logolás (ha a reconnect_handler elérhető)
try:
    # Próbáljuk meg relatívan importálni
    from .reconnect_handler import log_event
except ImportError:
    # Vagy abszolútan, ha a core mappán kívülről hívják
    try:
        from core.reconnect_handler import log_event
    except ImportError:
        # Dummy logger végső esetben
        def log_event(msg):
            print(f"[LOG - Dummy ConfigManager]: {msg}")


SETTINGS_FILE = "led_settings.json"

DEFAULT_SETTINGS = {
    "start_with_windows": False,
    "last_device_address": None,
    "last_device_name": None,  # Hozzáadva a név is
    "auto_connect_on_startup": True,  # Új beállítás: automatikus csatlakozás induláskor
    "brightness_level": 80,  # Fényerő százalékos értéke (0-100)
}


def _get_settings_path():
    """Visszaadja a beállítások fájl teljes elérési útját."""
    BASE_DIR.mkdir(parents=True, exist_ok=True)
    return str(BASE_DIR / SETTINGS_FILE)


def load_settings():
    """Betölti a beállításokat a JSON fájlból."""
    path = _get_settings_path()
    settings = DEFAULT_SETTINGS.copy()  # Kezdjük az alapértelmezettel
    if os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                loaded_data = json.load(f)
            # Csak azokat a kulcsokat fogadjuk el, amik a DEFAULT_SETTINGS-ben is benne vannak
            # és a típusuk is megfelelő (kivéve, ha az alapértelmezett None)
            for key in DEFAULT_SETTINGS:
                if key in loaded_data:
                    default_value = DEFAULT_SETTINGS[key]
                    loaded_value = loaded_data[key]
                    expected_type = type(default_value)

                    # Típusellenőrzés (None megengedő)
                    type_is_ok = False
                    if default_value is None:
                        type_is_ok = isinstance(loaded_value, (str, type(None)))
                    else:
                        type_is_ok = isinstance(loaded_value, expected_type)

                    if type_is_ok:
                        settings[key] = loaded_value
                    else:
                        log_event(
                            f"Figyelmeztetés: Érvénytelen típus a '{key}' beállításnál a {path}-ban. Várt (alap): {expected_type}, Kapott: {type(loaded_value)}. Alapértelmezett érték használva."
                        )
                # Ha a kulcs nincs a betöltött adatokban, az alapértelmezett marad
            log_event(f"Beállítások betöltve: {path}")
            log_event(f"Betöltött értékek: {settings}")  # Debug log
        except json.JSONDecodeError:
            log_event(f"Hiba: A {path} fájl hibás JSON formátumú. Alapértelmezett beállítások használva.")
            settings = DEFAULT_SETTINGS.copy()  # Biztosítjuk az alapértelmezett értékeket
        except Exception as e:
            log_event(f"Hiba a beállítások betöltésekor ({path}): {e}. Alapértelmezett beállítások használva.")
            traceback.print_exc()  # Részletes hiba kiírása
            settings = DEFAULT_SETTINGS.copy()  # Biztosítjuk az alapértelmezett értékeket
    else:
        log_event(f"Nincs mentett beállítás ({path}), alapértelmezett beállítások használva.")
    return settings


# Betöltjük egyszer indításkor, és ezt használjuk a program futása során
CURRENT_SETTINGS = load_settings()


def get_setting(key):
    """Visszaad egy beállítási értéket a memóriából."""
    # Használja a már betöltött CURRENT_SETTINGS-et
    return CURRENT_SETTINGS.get(key, DEFAULT_SETTINGS.get(key))


def set_setting(key, value):
    """Beállít egy értéket a memóriában és elmenti a fájlba."""
    if key not in DEFAULT_SETTINGS:
        log_event(f"HIBA: Ismeretlen beállítási kulcs: {key}")
        return

    default_value = DEFAULT_SETTINGS[key]
    expected_type = type(default_value)

    # Típusellenőrzés módosítása:
    type_is_ok = False
    if default_value is None:
        # Ha az alapértelmezett None, akkor None vagy string elfogadható
        type_is_ok = isinstance(value, (str, type(None)))
    else:
        # Különben a típusnak pontosan meg kell egyeznie (pl. bool, int)
        type_is_ok = isinstance(value, expected_type)

    if type_is_ok:
        # Érték frissítése a memóriában
        CURRENT_SETTINGS[key] = value
        # Tényleges mentés fájlba
        path = _get_settings_path()
        # Biztosítjuk, hogy csak az ismert kulcsokat mentsük, az aktuális értékekkel
        settings_to_save = {k: CURRENT_SETTINGS.get(k, DEFAULT_SETTINGS[k]) for k in DEFAULT_SETTINGS}
        try:
            with open(path, "w", encoding="utf-8") as f:
                json.dump(settings_to_save, f, ensure_ascii=False, indent=4)
            log_event(f"Beállítások elmentve ({key}={value}): {path}")
        except Exception as e:
            log_event(f"Hiba a beállítások mentésekor ({path}): {e}")
            traceback.print_exc()
    else:
        log_event(
            f"Figyelmeztetés: Típuseltérés a '{key}' beállítás mentésekor. Várt (alap): {expected_type}, Kapott: {type(value)}. Mentés kihagyva."
        )
