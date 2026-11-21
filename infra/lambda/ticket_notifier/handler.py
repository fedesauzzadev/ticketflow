"""Lambda ticket-notifier (N-4002): S3 ObjectCreated del QR de una entrada -> notificación.

Ticketing guarda el QR en s3://ticketflow-tickets/qr/{purchase_id}/{ticket_id}.json; S3
invoca esta función y la "notificación" es una línea JSON en CloudWatch Logs.
"""
import json
import re
from urllib.parse import unquote_plus

QR_KEY = re.compile(r"qr/([^/]+)/([^/]+)\.json")


def handler(event, context):
    notified = []
    records = event.get("Records") if isinstance(event, dict) else None
    for record in records or []:
        try:
            s3 = record["s3"]
            key = unquote_plus(s3["object"]["key"])  # S3 url-encodea la key en el evento
            match = QR_KEY.fullmatch(key)
        except (KeyError, TypeError, AttributeError):  # evento malformado: se ignora, no se relanza
            match = None
        if not match:
            print(json.dumps({"skipped": str(record)[:200]}))
            continue
        item = {
            "notification": "tickets-issued",
            "purchase_id": match.group(1),
            "ticket_id": match.group(2),
            "bucket": (s3.get("bucket") or {}).get("name"),
        }
        print(json.dumps(item))
        notified.append(item)
    return {"notified": len(notified)}
