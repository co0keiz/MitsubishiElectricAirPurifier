"""Integration constants."""
from datetime import timedelta

DOMAIN = "ma_ew85s_local"
MODEL = "MA-EW85S-E"
PLATFORMS = ["fan", "switch", "button", "binary_sensor", "sensor"]
UPDATE_INTERVAL = timedelta(seconds=30)
