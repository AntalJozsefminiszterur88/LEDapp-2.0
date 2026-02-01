from pathlib import Path

# Logolás (ha elérhető)
try:
    from core.reconnect_handler import log_event
except ImportError:
    try:
        from .core.reconnect_handler import log_event
    except Exception:

        def log_event(msg):
            print(f"[LOG - Dummy Config]: {msg}")


LATITUDE = 47.4338
LONGITUDE = 19.1931
TIMEZONE = "UTC+2"

# Alap könyvtár: Dokumentumok/UMKGL Solutions/LEDapp
BASE_DIR = Path.home() / "Documents" / "UMKGL Solutions" / "LEDapp"
BASE_DIR.mkdir(parents=True, exist_ok=True)

# Az ütemezés fájl teljes elérési útja
CONFIG_FILE = str(BASE_DIR / "led_schedule.json")
# Új fájl az ütemezési profilok tárolásához
PROFILES_FILE = str(BASE_DIR / "led_schedule_profiles.json")

CHARACTERISTIC_UUID = "0000fff3-0000-1000-8000-00805f9b34fb"

DAYS = ["Hétfő", "Kedd", "Szerda", "Csütörtök", "Péntek", "Szombat", "Vasárnap"]

DEFAULT_COLORS = [
    ("Piros", "#ff0000", "7e000503ff000000ef"),
    ("Zöld", "#00ff00", "7e00050300ff0000ef"),
    ("Kék", "#0000ff", "7e0005030000ff00ef"),
    ("Sárga", "#ffff00", "7e000503ffff0000ef"),
    ("Cian", "#00ffff", "7e00050300ffff00ef"),
    ("Lila", "#800080", "7e00050380008000ef"),
    ("Narancs", "#ffa500", "7e000503ffa50000ef"),
    ("Fehér", "#ffffff", "7e000503ffffff00ef"),
]

CUSTOM_COLORS_FILE = str(BASE_DIR / "custom_colors.json")


def _load_custom_colors():
    if Path(CUSTOM_COLORS_FILE).exists():
        try:
            import json

            with open(CUSTOM_COLORS_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
            colors = []
            for item in data:
                if isinstance(item, dict) and "name" in item and "hex" in item:
                    hex_val = item["hex"].lstrip("#")
                    cmd = f"7e000503{hex_val}00ef"
                    colors.append((item["name"], f"#{hex_val}", cmd))
            return colors
        except Exception as e:
            log_event(f"Hiba a saját színek betöltésekor ({CUSTOM_COLORS_FILE}): {e}")
            return []
    return []


CUSTOM_COLORS = _load_custom_colors()


COLORS = DEFAULT_COLORS + CUSTOM_COLORS

# Gyors leképezés szín hex -> parancs
HEX_TO_COMMAND = {c[1].lower(): c[2] for c in COLORS}


def hex_to_command(hex_color: str) -> str | None:
    """Adott hex sztringhez visszaadja a küldendő parancsot."""
    if not hex_color:
        return None
    key = hex_color.lower()
    if not key.startswith("#"):
        key = f"#{key}"
    return HEX_TO_COMMAND.get(key)

