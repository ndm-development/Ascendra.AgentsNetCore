#!/usr/bin/env python3
"""Valida e importa promesas históricas a PostgreSQL sin exponer secretos.

Uso típico:
    python3 scripts/importar_promesas_excel.py --excel /ruta/Promesas.xlsx --seco
    python3 scripts/importar_promesas_excel.py --excel /ruta/Promesas.xlsx \
      --connection-notebook /ruta/PostgreSQL_Ascendra.ipynb --aplicar

El identificador original del archivo se guarda en ``id_promesa_origen``. La
llave interna ``id_promesa`` la genera PostgreSQL, de modo que las promesas que
se registren posteriormente en Ascendra no pueden colisionar con los IDs de
origen.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import unicodedata
from dataclasses import dataclass
from datetime import date, datetime, time, timezone
from decimal import Decimal, InvalidOperation
from pathlib import Path
from typing import Any
from zoneinfo import ZoneInfo

from openpyxl import load_workbook
from psycopg import connect


MEXICO_CITY = ZoneInfo("America/Mexico_City")
REQUIRED_COLUMNS = (
    "IdPromesa",
    "Cliente_Unico",
    "Numero_Cliente",
    "Fecha_Promesa",
    "Fecha_Creacion",
    "Numero_Semanas",
)
SOURCE_COLUMNS = (
    "IdPromesa",
    "Cliente_Unico",
    "Agente",
    "Numero_Cliente",
    "Fecha_Promesa",
    "Monto_Inicial",
    "Monto_Semanal",
    "Tipo_Promesa",
    "Numero_Semanas",
    "Fecha_Creacion",
    "UsuAlta",
)


@dataclass(frozen=True)
class Promise:
    source_id: str
    client_id: str
    agent: str | None
    client_number: str
    promise_date: date
    initial_amount: Decimal | None
    weekly_amount: Decimal | None
    promise_type: str | None
    number_of_weeks: int
    created_at: datetime
    user_created: str | None


def normalize_header(value: object) -> str:
    text = unicodedata.normalize("NFKD", str(value or ""))
    text = "".join(char for char in text if not unicodedata.combining(char))
    return re.sub(r"[^A-Z0-9]+", "", text.upper())


def clean(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value).replace("\u00a0", " ").strip()


def optional_text(value: object) -> str | None:
    result = clean(value)
    return result or None


def parse_decimal(value: object, field: str) -> Decimal | None:
    text = clean(value)
    if not text or text.upper() in {"NULL", "N/A", "NA", "N/D", "NONE", "SIN DATO"}:
        return None
    normalized = re.sub(r"[^0-9,.-]", "", text).replace(",", "")
    try:
        return Decimal(normalized)
    except InvalidOperation as error:
        raise ValueError(f"{field} no es un importe válido") from error


def parse_integer(value: object, field: str) -> int:
    decimal_value = parse_decimal(value, field)
    if decimal_value is None or decimal_value != decimal_value.to_integral_value():
        raise ValueError(f"{field} debe ser un entero")
    return int(decimal_value)


def parse_date(value: object, field: str) -> date:
    if isinstance(value, datetime):
        return value.date()
    if isinstance(value, date):
        return value
    text = clean(value)
    for format_string in ("%Y-%m-%d", "%d/%m/%Y", "%Y/%m/%d"):
        try:
            return datetime.strptime(text, format_string).date()
        except ValueError:
            pass
    raise ValueError(f"{field} no es una fecha válida")


def parse_local_timestamp(value: object, field: str) -> datetime:
    if isinstance(value, datetime):
        timestamp = value
    elif isinstance(value, date):
        timestamp = datetime.combine(value, time.min)
    else:
        text = clean(value)
        timestamp = None
        for format_string in (
            "%Y-%m-%d %H:%M:%S.%f",
            "%Y-%m-%d %H:%M:%S",
            "%d/%m/%Y %H:%M:%S",
            "%d/%m/%Y",
        ):
            try:
                timestamp = datetime.strptime(text, format_string)
                break
            except ValueError:
                pass
        if timestamp is None:
            raise ValueError(f"{field} no es una fecha y hora válida")
    if timestamp.tzinfo is None:
        timestamp = timestamp.replace(tzinfo=MEXICO_CITY)
    return timestamp.astimezone(timezone.utc)


def normalize_type(value: object) -> str | None:
    original = optional_text(value)
    if original is None:
        return None
    comparable = normalize_header(original)
    return {
        "LIQUIDACION": "Liquidación",
        "PARCIAL": "Parcial",
        "PLANDEPAGO": "Plan de Pago",
        "RECURRENCIA": "Recurrencia",
        "RMD": "RMD",
    }.get(comparable, original)


def source_phone(value: object) -> str:
    raw = clean(value)
    digits = re.sub(r"\D", "", raw)
    if len(digits) == 13 and digits.startswith("521"):
        digits = digits[-10:]
    elif len(digits) == 12 and digits.startswith("52"):
        digits = digits[-10:]
    return digits or raw


def read_rows(path: Path) -> list[dict[str, object]]:
    if not path.is_file():
        raise FileNotFoundError(f"No existe el Excel: {path}")
    workbook = load_workbook(path, read_only=True, data_only=True)
    try:
        worksheet = workbook.active
        headers = next(worksheet.iter_rows(min_row=1, max_row=1, values_only=True), ())
        positions = {normalize_header(header): index for index, header in enumerate(headers)}
        missing = [name for name in REQUIRED_COLUMNS if normalize_header(name) not in positions]
        if missing:
            raise ValueError(f"Faltan columnas requeridas: {', '.join(missing)}")
        selected = {name: positions.get(normalize_header(name)) for name in SOURCE_COLUMNS}
        rows: list[dict[str, object]] = []
        for values in worksheet.iter_rows(min_row=2, values_only=True):
            if not any(value is not None and clean(value) for value in values):
                continue
            rows.append({
                name: values[index] if index is not None and index < len(values) else None
                for name, index in selected.items()
            })
        return rows
    finally:
        workbook.close()


def validate(rows: list[dict[str, object]]) -> tuple[list[Promise], list[dict[str, object]]]:
    promises: list[Promise] = []
    rejected: list[dict[str, object]] = []
    source_ids: set[str] = set()
    duplicated_ids: set[str] = set()

    for row_number, row in enumerate(rows, start=2):
        source_id = clean(row["IdPromesa"])
        if source_id in source_ids:
            duplicated_ids.add(source_id)
        source_ids.add(source_id)
        try:
            client_id = clean(row["Cliente_Unico"])
            client_number = source_phone(row["Numero_Cliente"])
            if not source_id or not client_id or not client_number:
                raise ValueError("faltan IdPromesa, Cliente_Unico o Numero_Cliente")
            number_of_weeks = parse_integer(row["Numero_Semanas"], "Numero_Semanas")
            if not 0 <= number_of_weeks <= 32767:
                raise ValueError("Numero_Semanas está fuera del rango permitido")
            initial_amount = parse_decimal(row["Monto_Inicial"], "Monto_Inicial")
            weekly_amount = parse_decimal(row["Monto_Semanal"], "Monto_Semanal")
            if (initial_amount is not None and initial_amount < 0) or (weekly_amount is not None and weekly_amount < 0):
                raise ValueError("contiene un monto negativo")
            promises.append(Promise(
                source_id=source_id,
                client_id=client_id,
                agent=optional_text(row["Agente"]),
                client_number=client_number,
                promise_date=parse_date(row["Fecha_Promesa"], "Fecha_Promesa"),
                initial_amount=initial_amount,
                weekly_amount=weekly_amount,
                promise_type=normalize_type(row["Tipo_Promesa"]),
                number_of_weeks=number_of_weeks,
                created_at=parse_local_timestamp(row["Fecha_Creacion"], "Fecha_Creacion"),
                user_created=optional_text(row["UsuAlta"]),
            ))
        except ValueError as error:
            rejected.append({"fila_excel": row_number, "id_promesa": source_id or None, "motivo": str(error)})

    if duplicated_ids:
        raise ValueError("Hay IdPromesa duplicados: " + ", ".join(sorted(duplicated_ids)[:10]))
    return promises, rejected


def database_url(args: argparse.Namespace) -> str:
    if args.database_url:
        return args.database_url
    if value := os.environ.get("ASCENDRA_DATABASE_URL"):
        return value
    if not args.connection_notebook:
        raise ValueError("Indica ASCENDRA_DATABASE_URL o --connection-notebook al aplicar la carga")
    content = args.connection_notebook.read_text(encoding="utf-8")
    candidates = re.findall(r"postgres(?:ql)?://[^\s\"']+", content)
    value = next((candidate for candidate in candidates if "@" in candidate), None)
    if value is None:
        raise ValueError("No se encontró una URL de PostgreSQL en el cuaderno indicado")
    return value.replace("\\/", "/")


def report_payload(rows: int, promises: list[Promise], rejected: list[dict[str, object]]) -> dict[str, object]:
    return {
        "filas_excel": rows,
        "filas_validas": len(promises),
        "filas_rechazadas": len(rejected),
        "rechazadas": rejected,
    }


def write_report(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def apply(promises: list[Promise], url: str) -> dict[str, int]:
    insert = """
        INSERT INTO promesas (
            cliente_unico, agente, numero_cliente, fecha_promesa,
            monto_inicial, monto_semanal, tipo_promesa, numero_semanas,
            fecha_creacion, usu_alta, origen, id_promesa_origen)
        VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, 'Historico', %s)
    """
    values = [(
        promise.client_id, promise.agent, promise.client_number, promise.promise_date,
        promise.initial_amount, promise.weekly_amount, promise.promise_type, promise.number_of_weeks,
        promise.created_at, promise.user_created, promise.source_id,
    ) for promise in promises]
    with connect(url, sslmode="require") as connection:
        with connection.cursor() as cursor:
            cursor.execute("SELECT COUNT(*) FROM promesas")
            existing = cursor.fetchone()[0]
            if existing:
                raise RuntimeError(
                    f"La tabla promesas contiene {existing} registros. La importación se canceló para no mezclar información."
                )
            cursor.execute("SELECT setval(pg_get_serial_sequence('promesas', 'id_promesa'), 1, false)")
            cursor.executemany(insert, values)
            cursor.execute("""
                SELECT COUNT(*), COUNT(DISTINCT id_promesa_origen), MIN(id_promesa), MAX(id_promesa)
                FROM promesas
                WHERE origen = 'Historico'
            """)
            count, distinct_source_ids, min_internal_id, max_internal_id = cursor.fetchone()
        connection.commit()
    return {
        "insertadas": count,
        "ids_origen_distintos": distinct_source_ids,
        "id_interno_minimo": min_internal_id or 0,
        "id_interno_maximo": max_internal_id or 0,
    }


def parser() -> argparse.ArgumentParser:
    result = argparse.ArgumentParser(description="Importa un Excel de promesas históricas a PostgreSQL.")
    result.add_argument("--excel", type=Path, required=True, help="Archivo Promesas.xlsx de entrada.")
    result.add_argument("--seco", action="store_true", help="Solo valida; no requiere ni abre la base de datos.")
    result.add_argument("--aplicar", action="store_true", help="Confirma la inserción transaccional en PostgreSQL.")
    result.add_argument("--database-url", help="URL de PostgreSQL. Es preferible usar ASCENDRA_DATABASE_URL.")
    result.add_argument("--connection-notebook", type=Path, help="Cuaderno local que ya contiene la conexión, sin imprimirla.")
    result.add_argument("--reporte", type=Path, help="JSON con las filas excluidas y el resultado final.")
    return result


def main() -> None:
    args = parser().parse_args()
    if args.seco == args.aplicar:
        raise SystemExit("Usa exactamente una modalidad: --seco o --aplicar.")

    rows = read_rows(args.excel.resolve())
    promises, rejected = validate(rows)
    report = report_payload(len(rows), promises, rejected)
    if args.seco:
        print(json.dumps(report, ensure_ascii=False, indent=2))
        if args.reporte:
            write_report(args.reporte.resolve(), report)
        return

    outcome = apply(promises, database_url(args))
    report["resultado"] = outcome
    if args.reporte:
        write_report(args.reporte.resolve(), report)
    print(json.dumps({key: value for key, value in report.items() if key != "rechazadas"}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
