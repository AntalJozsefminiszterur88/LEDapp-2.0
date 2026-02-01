# LEDapp/gui/gui2_schedule_logic.py

import json
import os
from datetime import datetime, timedelta, time as dt_time
import traceback
import pytz

from PySide6.QtWidgets import QMessageBox

# Importáljuk a szükséges konfigurációs és backend/core elemeket
from config import COLORS, DAYS, CONFIG_FILE, PROFILES_FILE, hex_to_command
from core.sun_logic import DAYS_HU, get_local_sun_info as _core_get_local_sun_info
from core.location_utils import get_sun_times  # noqa: F401

# --- Időzóna Definíció ---
# Biztosítjuk, hogy a LOCAL_TZ létezzen
try:
    # Próbáljuk meg a rendszer alapértelmezett időzónáját használni, ha lehetséges
    try:
        import tzlocal

        LOCAL_TZ = tzlocal.get_localzone()
        print(f"Helyi időzóna (tzlocal - logic): {LOCAL_TZ.zone}")
    except ImportError:
        # Ha a tzlocal nincs telepítve, maradunk a fixnél
        LOCAL_TZ = pytz.timezone("Europe/Budapest")
        print(f"Helyi időzóna (fix - logic): {LOCAL_TZ.zone}")
except pytz.UnknownTimeZoneError:
    print("Figyelmeztetés: Helyi időzóna nem található. UTC használata (logic).")
    LOCAL_TZ = pytz.utc
except Exception as e:
    print(f"Váratlan hiba az időzóna beolvasásakor (logic): {e}. UTC használata.")
    LOCAL_TZ = pytz.utc

# --- Logika Függvények ---


def get_local_sun_info():
    """Wrapper around core.sun_logic.get_local_sun_info."""
    return _core_get_local_sun_info()


def get_default_schedule():
    """Alapértelmezett ütemezés létrehozása."""
    return {
        day: {
            "color": COLORS[0][0] if COLORS else "",
            "on_time": "",
            "off_time": "",
            "sunrise": False,
            "sunrise_offset": 0,
            "sunset": False,
            "sunset_offset": 0,
        }
        for day in DAYS
    }


def load_profiles_from_file(main_app):
    """Betölti az ütemezési profilokat."""
    default_schedule = get_default_schedule()

    if os.path.exists(PROFILES_FILE):
        try:
            with open(PROFILES_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)

            profiles = {}
            for name, prof in data.items():
                active = bool(prof.get("active", True))
                sched = prof.get("schedule", {})
                merged = {}
                for day in DAYS:
                    d = default_schedule[day].copy()
                    if day in sched and isinstance(sched[day], dict):
                        for key in d:
                            if key in sched[day]:
                                val = sched[day][key]
                                if key.endswith("_offset"):
                                    try:
                                        d[key] = int(val)
                                    except (ValueError, TypeError):
                                        d[key] = 0
                                elif isinstance(val, type(d[key])):
                                    d[key] = val
                    merged[day] = d
                profiles[name] = {"active": active, "schedule": merged}

            if profiles:
                main_app.profiles = profiles
                return
        except Exception as e:
            print(f"Hiba a profilok betöltésekor: {e}")

    # Visszafelé kompatibilitás: régi egyprofilos fájl
    single_schedule = default_schedule.copy()
    if os.path.exists(CONFIG_FILE):
        try:
            with open(CONFIG_FILE, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            for day in DAYS:
                d = single_schedule[day].copy()
                if day in loaded and isinstance(loaded[day], dict):
                    for key in d:
                        if key in loaded[day]:
                            val = loaded[day][key]
                            if key.endswith("_offset"):
                                try:
                                    d[key] = int(val)
                                except (ValueError, TypeError):
                                    d[key] = 0
                            elif isinstance(val, type(d[key])):
                                d[key] = val
                single_schedule[day] = d
        except Exception as e:
            print(f"Hiba a régi ütemezés betöltésekor: {e}")

    main_app.profiles = {"Alapértelmezett": {"active": True, "schedule": single_schedule}}


def _save_profiles_to_file(main_app):
    """Segédfüggvény a profilok mentéséhez."""
    try:
        with open(PROFILES_FILE, "w", encoding="utf-8") as f:
            json.dump(main_app.profiles, f, ensure_ascii=False, indent=4)
        return True
    except Exception as e:
        print(f"Hiba a profilok mentésekor: {e}")
        return False


def check_profile_conflicts(main_app, target_name):
    """Egyszerű ütközésvizsgálat az aktivált profilok között.

    Csak az explicit on/off időket hasonlítjuk össze. A napkelte/napnyugta
    alapú beállításokat nem ellenőrizzük, mert azok pontos ideje helyfüggő.

    Args:
        main_app: Az alkalmazás fő objektuma, amely tartalmazza a profilokat.
        target_name: A vizsgálandó profil neve.

    Returns:
        List[str]: Napok és profilnevek, amelyek ütköznek a célprofillal.
    """

    target = main_app.profiles.get(target_name)
    if not target:
        return []

    def parse_interval(day_data):
        if not day_data:
            return None
        if day_data.get("sunrise") or day_data.get("sunset"):
            return None
        on_str = day_data.get("on_time")
        off_str = day_data.get("off_time")
        if not on_str or not off_str:
            return None
        try:
            on_t = dt_time.fromisoformat(on_str)
            off_t = dt_time.fromisoformat(off_str)
        except ValueError:
            return None
        on_m = on_t.hour * 60 + on_t.minute
        off_m = off_t.hour * 60 + off_t.minute
        if off_m <= on_m:
            off_m += 24 * 60
        return on_m, off_m

    conflicts = []
    for other_name, other in main_app.profiles.items():
        if other_name == target_name or not other.get("active", False):
            continue
        for day in DAYS:
            int1 = parse_interval(target.get("schedule", {}).get(day, {}))
            int2 = parse_interval(other.get("schedule", {}).get(day, {}))
            if not int1 or not int2:
                continue
            s1, e1 = int1
            s2, e2 = int2
            if s1 < e2 and s2 < e1:
                conflicts.append(f"{day} - {other_name}")
                break
    return conflicts


def get_profile_day_intervals(main_app, profile_name):
    """Return schedule intervals in minutes for drawing a timeline."""

    profile = main_app.profiles.get(profile_name)
    if not profile:
        return {}

    result = {}
    schedule = profile.get("schedule", {})
    today = datetime.now(LOCAL_TZ).date()
    today_idx = today.weekday()

    for idx, day in enumerate(DAYS):
        data = schedule.get(day, {})
        intervals = []
        ref_date = today + timedelta(days=(idx - today_idx) % 7)
        on_dt, off_dt = None, None

        if data.get("sunrise"):
            sr, _ = get_sun_times(main_app.latitude, main_app.longitude, datetime.combine(ref_date, dt_time()))
            if sr:
                try:
                    on_dt = sr + timedelta(minutes=int(data.get("sunrise_offset", 0)))
                except Exception:
                    on_dt = sr
        else:
            on_str = data.get("on_time")
            if on_str:
                try:
                    on_dt = LOCAL_TZ.localize(datetime.combine(ref_date, dt_time.fromisoformat(on_str)))
                except Exception:
                    pass

        if data.get("sunset"):
            _, ss = get_sun_times(main_app.latitude, main_app.longitude, datetime.combine(ref_date, dt_time()))
            if ss:
                try:
                    off_dt = ss + timedelta(minutes=int(data.get("sunset_offset", 0)))
                except Exception:
                    off_dt = ss
        else:
            off_str = data.get("off_time")
            if off_str:
                try:
                    off_dt = LOCAL_TZ.localize(datetime.combine(ref_date, dt_time.fromisoformat(off_str)))
                except Exception:
                    pass

        if on_dt and off_dt:
            if off_dt <= on_dt:
                off_dt += timedelta(days=1)

            start_min = on_dt.hour * 60 + on_dt.minute
            end_min = off_dt.hour * 60 + off_dt.minute

            color_name = data.get("color", "")
            color_hex = next((c[1] for c in COLORS if c[0] == color_name), "#ffffff")

            if end_min > 24 * 60:
                intervals.append((start_min, 24 * 60, color_hex))
                intervals.append((0, end_min - 24 * 60, color_hex))
            else:
                intervals.append((start_min, end_min, color_hex))

        result[day] = intervals

    return result


def get_all_profiles_day_intervals(main_app):
    """Return combined schedule intervals for all profiles."""
    combined = {day: [] for day in DAYS}
    for name in getattr(main_app, "profiles", {}):
        prof_intervals = get_profile_day_intervals(main_app, name)
        for day, intervals in prof_intervals.items():
            combined[day].extend(intervals)
    return combined


def save_profile(gui_widget):
    """
    Elmenti az aktuális ütemezési beállításokat JSON fájlba a GUI widgetek alapján.
    Args:
        gui_widget: A GUI2_Widget példánya.
    """
    schedule_to_save = {}
    valid = True
    for day, widgets in gui_widget.schedule_widgets.items():
        temp_data = {}
        try:
            temp_data["color"] = widgets["color"].currentText()
            if not temp_data["color"] and COLORS:
                temp_data["color"] = COLORS[0][0]

            temp_data["sunrise"] = widgets["sunrise"].isChecked()
            temp_data["sunset"] = widgets["sunset"].isChecked()

            offset_sr_str = widgets["sunrise_offset"].text()
            offset_ss_str = widgets["sunset_offset"].text()
            # Ellenőrizzük, hogy az offset szám-e
            temp_data["sunrise_offset"] = int(offset_sr_str) if offset_sr_str else 0
            temp_data["sunset_offset"] = int(offset_ss_str) if offset_ss_str else 0

            on_time_val = widgets["on_time"].currentText()
            off_time_val = widgets["off_time"].currentText()
            temp_data["on_time"] = ""
            temp_data["off_time"] = ""

            # Validáljuk a HH:MM formátumot, ha nincs napkelte/napnyugta bejelölve
            if not temp_data["sunrise"] and on_time_val:
                try:
                    dt_time.fromisoformat(on_time_val)
                    temp_data["on_time"] = on_time_val
                except ValueError:
                    raise ValueError(
                        f"Érvénytelen bekapcsolási idő formátum: '{on_time_val}'. HH:MM formátum szükséges."
                    )

            if not temp_data["sunset"] and off_time_val:
                try:
                    dt_time.fromisoformat(off_time_val)
                    temp_data["off_time"] = off_time_val
                except ValueError:
                    raise ValueError(
                        f"Érvénytelen kikapcsolási idő formátum: '{off_time_val}'. HH:MM formátum szükséges."
                    )

            schedule_to_save[day] = temp_data

        except ValueError as ve:
            QMessageBox.critical(
                gui_widget,
                "Hiba",
                f"Érvénytelen érték a '{day}' napnál: {ve}. Kérlek javítsd (idő HH:MM, offset egész szám).",
            )
            valid = False
            break
        except Exception as e:
            QMessageBox.critical(gui_widget, "Hiba", f"Váratlan hiba a '{day}' nap feldolgozásakor: {e}")
            valid = False
            break

    if not valid:
        return

    try:
        profile_name = getattr(gui_widget, "current_profile_name", None)
        if not profile_name:
            QMessageBox.critical(gui_widget, "Hiba", "Nincs kiválasztott profil a mentéshez.")
            return

        gui_widget.main_app.profiles[profile_name]["schedule"] = schedule_to_save
        if _save_profiles_to_file(gui_widget.main_app):
            QMessageBox.information(gui_widget, "Mentés sikeres", "Az ütemezés sikeresen elmentve.")
            gui_widget.main_app.schedule = schedule_to_save
            gui_widget.unsaved_changes = False
        else:
            QMessageBox.critical(gui_widget, "Mentési hiba", "Nem sikerült a profilok mentése.")
    except Exception as e:
        QMessageBox.critical(gui_widget, "Mentési hiba", f"Hiba történt a mentés során: {e}")


def check_profiles(gui_widget):
    """Aktív ütemezési profilok ellenőrzése és LED vezérlése."""
    now_local = datetime.now(LOCAL_TZ)
    today_date = now_local.date()
    today_name_hu = DAYS_HU.get(now_local.strftime("%A"), now_local.strftime("%A"))

    # Előző nap neve (magyarul) a keresztbe nyúló intervallumok kezeléséhez
    yesterday = now_local - timedelta(days=1)
    yesterday_name_hu = DAYS_HU.get(yesterday.strftime("%A"), yesterday.strftime("%A"))

    main_app = gui_widget.main_app
    desired_hex = None
    schedule_entries_found = False
    intervals = []

    def parse_day_entry(day_data, ref_date):
        """Visszaadja az on/off datetime objektumokat egy adott dátumhoz."""
        if not day_data:
            return None, None

        on_time_dt = None
        off_time_dt = None

        # Napkelte vagy manuális on_time
        if day_data.get("sunrise"):
            # Csak a mai napra van napkelte adatunk
            if ref_date == today_date and main_app.sunrise:
                try:
                    offset = int(day_data.get("sunrise_offset", 0))
                    on_time_dt = main_app.sunrise + timedelta(minutes=offset)
                except (ValueError, TypeError):
                    pass
        else:
            on_str = day_data.get("on_time")
            if on_str:
                try:
                    time_obj = dt_time.fromisoformat(on_str)
                    on_time_dt = LOCAL_TZ.localize(datetime.combine(ref_date, time_obj))
                except ValueError:
                    pass

        # Napnyugta vagy manuális off_time
        if day_data.get("sunset"):
            if ref_date == today_date and main_app.sunset:
                try:
                    offset = int(day_data.get("sunset_offset", 0))
                    off_time_dt = main_app.sunset + timedelta(minutes=offset)
                except (ValueError, TypeError):
                    pass
        else:
            off_str = day_data.get("off_time")
            if off_str:
                try:
                    time_obj = dt_time.fromisoformat(off_str)
                    off_time_dt = LOCAL_TZ.localize(datetime.combine(ref_date, time_obj))
                except ValueError:
                    pass

        if on_time_dt and off_time_dt and off_time_dt <= on_time_dt:
            off_time_dt += timedelta(days=1)

        return on_time_dt, off_time_dt

    try:
        for name, prof in main_app.profiles.items():
            if not prof.get("active", False):
                continue

            # Először vizsgáljuk az előző napot, majd a mait
            for day_name, ref_date in (
                (yesterday_name_hu, yesterday.date()),
                (today_name_hu, today_date),
            ):
                day_data = prof.get("schedule", {}).get(day_name)
                on_time_dt, off_time_dt = parse_day_entry(day_data, ref_date)

                if not on_time_dt or not off_time_dt:
                    continue

                schedule_entries_found = True
                target_color_name = day_data.get("color", "")
                target_color_hex = next((c[2] for c in COLORS if c[0] == target_color_name), None)
                if not target_color_hex:
                    continue

                intervals.append((on_time_dt, off_time_dt, target_color_hex))

        if intervals:
            intervals.sort(key=lambda x: x[0])
            for start, end, hex_color in intervals:
                if start <= now_local < end:
                    desired_hex = hex_color
                    break

        if desired_hex:
            if not main_app.is_led_on or main_app.last_color_hex != desired_hex:
                if gui_widget.controls_widget:
                    gui_widget.controls_widget.send_color_command(desired_hex)
        else:
            if schedule_entries_found and main_app.is_led_on and gui_widget.controls_widget:
                gui_widget.controls_widget.turn_off_led()

    except Exception as e:
        print(f"Váratlan hiba a profilok ellenőrzésekor: {e}")
        traceback.print_exc()


def get_color_by_visualization(main_app, now_local=None):
    """Return color hex for the current time using visualization intervals."""
    if now_local is None:
        now_local = datetime.now(LOCAL_TZ)
    day_name = DAYS_HU.get(now_local.strftime("%A"), now_local.strftime("%A"))
    total_min = now_local.hour * 60 + now_local.minute

    intervals = get_all_profiles_day_intervals(main_app)
    for start, end, color_hex in intervals.get(day_name, []):
        if start <= total_min < end:
            return color_hex
    return None


def apply_color_from_visualization(gui_widget):
    """Update LED state based on the schedule visualization."""
    main_app = gui_widget.main_app
    desired_hex = get_color_by_visualization(main_app)
    cmd = hex_to_command(desired_hex) if desired_hex else None

    if cmd:
        if not main_app.is_led_on or main_app.last_color_hex != cmd:
            if gui_widget.controls_widget:
                gui_widget.controls_widget.send_color_command(cmd)
    else:
        if main_app.is_led_on and gui_widget.controls_widget:
            gui_widget.controls_widget.turn_off_led()
