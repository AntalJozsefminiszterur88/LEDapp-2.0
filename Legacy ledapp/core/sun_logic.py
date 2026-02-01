from datetime import datetime
from core.location_utils import get_coordinates, get_sun_times

DAYS_HU = {
    "Monday": "Hétfő",
    "Tuesday": "Kedd",
    "Wednesday": "Szerda",
    "Thursday": "Csütörtök",
    "Friday": "Péntek",
    "Saturday": "Szombat",
    "Sunday": "Vasárnap",
}


def get_local_sun_info():
    lat, lon, located = get_coordinates()
    sunrise, sunset = get_sun_times(lat, lon)
    return {
        "latitude": lat,
        "longitude": lon,
        "sunrise": sunrise,
        "sunset": sunset,
        "located": located,
    }


def get_hungarian_day_name():
    english_day = datetime.now().strftime("%A")
    return DAYS_HU.get(english_day, english_day)
