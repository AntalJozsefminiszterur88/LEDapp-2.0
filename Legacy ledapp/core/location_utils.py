# core/location_utils.py (Részletesebb hibalogolással)

import requests
from datetime import datetime
from suntime import Sun
from pytz import timezone as pytz_timezone
import traceback  # Importáljuk a tracebacket

# Logolás importálása (ha a reconnect_handler definiálja)
try:
    from .reconnect_handler import log_event
except ImportError:
    # Dummy log_event ha a reconnect_handler nem elérhető innen
    def log_event(msg):
        print(f"[LOG - Dummy LocUtils]: {msg}")


BUDAPEST_COORDS = (47.4338, 19.1931)
LOCAL_TZ = pytz_timezone("Europe/Budapest")  # Feltételezzük, hogy ez létezik
UTC_TZ = pytz_timezone("UTC")


def _fetch_coords_ipapi():
    """Lekérdezi a koordinátákat az ip-api.com szolgáltatásból."""
    headers = {"User-Agent": "LEDApp/1.0"}
    response = requests.get("http://ip-api.com/json/", timeout=5, headers=headers)
    response.raise_for_status()
    data = response.json()
    if data.get("status") != "success":
        raise RuntimeError(data.get("message", "unknown error"))
    return data["lat"], data["lon"]


def _fetch_coords_ipinfo():
    """Lekérdezi a koordinátákat az ipinfo.io szolgáltatásból."""
    headers = {"User-Agent": "LEDApp/1.0"}
    response = requests.get("https://ipinfo.io/json", timeout=5, headers=headers)
    response.raise_for_status()
    data = response.json()
    if "loc" not in data:
        raise RuntimeError("loc missing")
    lat_str, lon_str = data["loc"].split(",")
    return float(lat_str), float(lon_str)


def _fetch_coords_ipwhois():
    """Lekérdezi a koordinátákat az ipwho.is szolgáltatásból."""
    headers = {"User-Agent": "LEDApp/1.0"}
    response = requests.get("https://ipwho.is/", timeout=5, headers=headers)
    response.raise_for_status()
    data = response.json()
    if not data.get("success", False):
        raise RuntimeError(data.get("message", "unknown error"))
    return data["latitude"], data["longitude"]


def _fetch_coords_ipapico():
    """Lekérdezi a koordinátákat az ipapi.co szolgáltatásból."""
    headers = {"User-Agent": "LEDApp/1.0"}
    response = requests.get("https://ipapi.co/json/", timeout=5, headers=headers)
    response.raise_for_status()
    data = response.json()
    if "latitude" not in data or "longitude" not in data:
        raise RuntimeError("latitude/longitude missing")
    return data["latitude"], data["longitude"]


def _fetch_coords_geolocationdb():
    """Lekérdezi a koordinátákat a geolocation-db.com szolgáltatásból."""
    headers = {"User-Agent": "LEDApp/1.0"}
    response = requests.get("https://geolocation-db.com/json/", timeout=5, headers=headers)
    response.raise_for_status()
    data = response.json()
    if "latitude" not in data or "longitude" not in data:
        raise RuntimeError("latitude/longitude missing")
    return float(data["latitude"]), float(data["longitude"])


def get_coordinates():
    """Megpróbálja lekérni a koordinátákat több forrásból."""
    for fetcher, name in (
        (_fetch_coords_ipapi, "ip-api.com"),
        (_fetch_coords_ipinfo, "ipinfo.io"),
        (_fetch_coords_ipwhois, "ipwho.is"),
        (_fetch_coords_ipapico, "ipapi.co"),
        (_fetch_coords_geolocationdb, "geolocation-db.com"),
    ):
        try:
            log_event(f"Koordináták lekérése ({name})...")
            lat, lon = fetcher()
            log_event(f"Koordináták sikeresen lekérve: Lat={lat}, Lon={lon}")
            return lat, lon, True
        except Exception as e:
            log_event(f"Hiba a {name} szolgáltatásból: {e}")
    log_event("Minden helymeghatározási próbálkozás sikertelen, alapértelmezett koordináták használata")
    log_event(f"Traceback:\n{traceback.format_exc()}")
    return BUDAPEST_COORDS[0], BUDAPEST_COORDS[1], False


def get_sun_times(lat, lon, now=None):
    """Kiszámolja a napkelte/napnyugta időpontokat a megadott koordinátákra."""
    try:
        # Dátum objektum a now alapján, vagy a mai nap, ha nincs megadva
        target_date = now.date() if now else datetime.now(LOCAL_TZ).date()

        sun = Sun(lat, lon)
        # Használjuk a dátum objektumot a számításhoz
        sunrise_utc_dt = sun.get_sunrise_time(target_date, UTC_TZ)
        sunset_utc_dt = sun.get_sunset_time(target_date, UTC_TZ)

        # Átváltás helyi időzónára
        sunrise_local = sunrise_utc_dt.astimezone(LOCAL_TZ)
        sunset_local = sunset_utc_dt.astimezone(LOCAL_TZ)
        log_event(
            f"Napkelte/Napnyugta számítva ({lat:.2f},{lon:.2f}): Kelte={sunrise_local.strftime('%H:%M')}, Nyugta={sunset_local.strftime('%H:%M')}"
        )
        return sunrise_local, sunset_local
    except Exception as e:
        log_event(f"Hiba a napkelte/napnyugta számítása közben: {e}")
        # traceback.print_exc() # Opcionális
        return None, None  # Hiba esetén None-t adunk vissza
