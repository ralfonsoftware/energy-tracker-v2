#!/usr/bin/env python3
"""Generates a reference-consistent v2 household export (docs/data-export-format.md) of a chosen size.

Mostly smartPlugReadings, the way a real household's export is: ~130,000 readings is roughly 32 MB.
Ids are derived from a fixed namespace, so the same arguments always produce the same file.

    scripts/gen-household-export.py --readings 130000 --out /tmp/export-32mb.json
"""
import argparse
import json
import uuid
from datetime import datetime, timedelta, timezone

NS = uuid.UUID("5d2f6c0e-3b7a-4e0f-9a51-0c1b6f2a7d11")


def gid(kind: str, n: int = 0) -> str:
    return str(uuid.uuid5(NS, f"{kind}:{n}"))


def stamp(moment: datetime) -> str:
    return moment.isoformat(timespec="seconds")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--readings", type=int, default=130_000, help="smartPlugReadings count")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    created = datetime(2026, 1, 1, tzinfo=timezone.utc)
    main_meter = gid("meter")
    rooms = [{"id": gid("room", i), "name": f"Room {i}", "createdAtUtc": stamp(created), "archivedAt": None} for i in range(4)]
    power_points = [
        {"id": gid("pp", i), "roomId": rooms[i % 4]["id"], "name": f"Outlet {i}", "createdAtUtc": stamp(created), "archivedAt": None}
        for i in range(8)
    ]
    devices = [
        {"id": gid("device", i), "powerPointId": power_points[i]["id"], "name": f"Device {i}", "createdAtUtc": stamp(created), "archivedAt": None}
        for i in range(8)
    ]
    meter_readings = [
        {
            "id": gid("reading", i), "mainMeterId": main_meter, "kwhValue": 1000 + i * 3.5,
            "readingTimestamp": stamp(created + timedelta(days=i)), "idempotencyKey": gid("idem", i),
            "createdAtUtc": stamp(created + timedelta(days=i)),
        }
        for i in range(120)
    ]

    plug_readings = []
    for i in range(args.readings):
        pp = power_points[i % 8]
        room = rooms[(i % 8) % 4]
        start = created + timedelta(minutes=10 * (i // 8))
        plug_readings.append({
            "id": gid("plug", i), "powerPointId": pp["id"], "roomName": room["name"], "powerPointName": pp["name"],
            "deviceName": f"Device {i % 8}", "intervalStart": stamp(start), "intervalEnd": stamp(start + timedelta(minutes=10)),
            "kwhValue": round(0.001 * (i % 97), 4),
        })

    document = {
        "formatVersion": "v2",
        "exportedAtUtc": stamp(created),
        "household": {
            "id": gid("household"), "createdAtUtc": stamp(created), "locale": "de-DE", "currency": "EUR",
            "yearlyBaselineKwh": 3500.0, "trendingThresholdKwh": 100.0, "lowConfidenceGapDays": 45,
            "tariffCheckCadenceMonths": 3, "aiPlausibilityEnabled": False, "aiPlausibilityBackendConfigured": False,
            "aiPlausibilityBackendLabel": None,
        },
        "householdMembers": [{"id": gid("member", i), "displayName": name} for i, name in enumerate(["Ralf", "Mia", "Jonas"])],
        "mainMeter": {"id": main_meter, "createdAtUtc": stamp(created), "digitCapacityKwh": None},
        "meterReadings": meter_readings,
        "meterRegressionPrompts": [],
        "tariffs": [{
            "id": gid("tariff"), "monthlyBaseFee": 12.5, "pricePerKwh": 0.31, "currency": "EUR",
            "contractStartDate": stamp(created), "contractPeriodMonths": 12, "createdAtUtc": stamp(created),
        }],
        "events": [],
        "rooms": rooms,
        "powerPoints": power_points,
        "devices": devices,
        "smartPlugReadings": plug_readings,
        "statusSnapshots": [],
        "auditCorrections": [],
    }

    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(document, handle, separators=(",", ":"))


if __name__ == "__main__":
    main()
