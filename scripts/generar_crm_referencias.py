#!/usr/bin/env python3
"""Genera el CRM compacto consumido por Referencias y WhatsApp.

No depende de Dash ni de ``app.py``: conserva el cruce operativo actual de
``Referencias.xlsx`` y ``Descarga_cartera.xlsx`` y genera atómicamente
``data/generated/CRM_Contactos.xlsx``.
"""

from __future__ import annotations

import argparse
import os
import re
import unicodedata
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from tempfile import NamedTemporaryFile
from typing import Any

from openpyxl import Workbook, load_workbook
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.worksheet.table import Table, TableStyleInfo


PROJECT_DIR = Path(__file__).resolve().parents[1]
CRM_FILENAME = "CRM_Contactos.xlsx"
RECORD_SHEET = "Registros"
REFERENCE_FIELDS = ("CLIENTE_UNICO", "Telefono 1", "Referencia", "Parentesco")
CARTERA_FIELDS = (
    "CLIENTE_UNICO",
    "NOMBRE_CTE",
    "PRODUCTO",
    "SALDO_TOTAL",
    "TELEFONO1",
    "TELEFONO2",
    "TELEFONO3",
    "TELEFONO4",
)
PHONE_FIELDS = CARTERA_FIELDS[4:]
EXPORT_HEADERS = (
    "CLIENTE_UNICO",
    "TELEFONO",
    "NOMBRE_REFERENCIA",
    "ORIGEN",
    "ESTATUS",
    "NOMBRE_CTE",
    "PRODUCTO",
    "SALDO_TOTAL",
    "PARENTESCO",
)


@dataclass(frozen=True)
class Contact:
    client_id: str
    phone: str
    reference_name: str
    origin: str
    status: str
    client_name: str
    product: str
    total_balance: float | None
    relationship: str


def column_key(value: object) -> str:
    """Compara encabezados sin importar espacios, acentos o guiones bajos."""
    normalized = unicodedata.normalize("NFKD", str(value or ""))
    no_accents = "".join(char for char in normalized if not unicodedata.combining(char))
    return re.sub(r"[^A-Z0-9]+", "", no_accents.upper())


def clean(value: object) -> str:
    if value is None:
        return ""
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    return str(value).replace("\u00a0", " ").strip()


def client_id(value: object) -> str:
    return clean(value)


def phone(value: object) -> str:
    digits = re.sub(r"\D", "", clean(value))
    if len(digits) == 13 and digits.startswith("521"):
        digits = digits[-10:]
    elif len(digits) == 12 and digits.startswith("52"):
        digits = digits[-10:]
    return "" if len(digits) < 7 or set(digits) == {"0"} else digits


def balance(value: object) -> float | None:
    raw = clean(value)
    if not raw or not re.search(r"\d", raw):
        return None
    normalized = re.sub(r"[^0-9,.-]", "", raw).replace(",", "")
    try:
        return float(normalized)
    except ValueError:
        return None


def argument_parser() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Cruza Referencias y Cartera para el CRM de Ascendra.")
    parser.add_argument("--referencias", type=Path, default=PROJECT_DIR / "data/input/Referencias.xlsx")
    parser.add_argument("--cartera", type=Path, default=PROJECT_DIR / "data/input/Descarga_cartera.xlsx")
    parser.add_argument("--salida", type=Path, default=PROJECT_DIR / f"data/generated/{CRM_FILENAME}")
    return parser.parse_args()


def read_rows(path: Path, required: tuple[str, ...]) -> list[dict[str, Any]]:
    if not path.is_file():
        raise FileNotFoundError(f"No existe el archivo de entrada: {path}")
    workbook = load_workbook(path, read_only=True, data_only=True)
    try:
        worksheet = workbook.active
        headers = next(worksheet.iter_rows(min_row=1, max_row=1, values_only=True), ())
        columns = {column_key(header): index for index, header in enumerate(headers)}
        selected = {field: columns.get(column_key(field)) for field in required}
        missing = [field for field, index in selected.items() if index is None]
        if missing:
            raise ValueError(f"{path.name} no tiene las columnas requeridas: {', '.join(missing)}.")
        maximum = max(index for index in selected.values() if index is not None) + 1
        return [
            {field: row[index] if index is not None and index < len(row) else None for field, index in selected.items()}
            for row in worksheet.iter_rows(min_row=2, max_col=maximum, values_only=True)
        ]
    finally:
        workbook.close()


def build_index(references: list[dict[str, Any]], cartera: list[dict[str, Any]]) -> tuple[list[Contact], dict[str, object]]:
    active_ids = {client_id(row["CLIENTE_UNICO"]) for row in cartera if client_id(row["CLIENTE_UNICO"])}
    details: dict[str, tuple[str, str, float | None]] = {}
    cartera_contacts: set[tuple[str, str]] = set()
    for row in cartera:
        current_id = client_id(row["CLIENTE_UNICO"])
        if not current_id:
            continue
        details.setdefault(current_id, (clean(row["NOMBRE_CTE"]), clean(row["PRODUCTO"]), balance(row["SALDO_TOTAL"])))
        for field in PHONE_FIELDS:
            normalized_phone = phone(row[field])
            if normalized_phone:
                cartera_contacts.add((current_id, normalized_phone))

    reference_records: set[tuple[str, str, str, str]] = set()
    reference_pairs: set[tuple[str, str]] = set()
    reference_ids: set[str] = set()
    for row in references:
        current_id = client_id(row["CLIENTE_UNICO"])
        normalized_phone = phone(row["Telefono 1"])
        if not current_id:
            continue
        reference_ids.add(current_id)
        if not normalized_phone:
            continue
        name = clean(row["Referencia"])
        relationship = clean(row["Parentesco"])
        reference_records.add((current_id, normalized_phone, name, relationship))
        reference_pairs.add((current_id, normalized_phone))

    contacts: list[Contact] = []
    for current_id, normalized_phone, name, relationship in reference_records:
        client_name, product, total_balance = details.get(current_id, ("", "", None))
        contacts.append(Contact(current_id, normalized_phone, name, "Referencia", "Activo" if current_id in active_ids else "Inactivo", client_name, product, total_balance, relationship))
    for current_id, normalized_phone in cartera_contacts - reference_pairs:
        client_name, product, total_balance = details.get(current_id, ("", "", None))
        contacts.append(Contact(current_id, normalized_phone, "", "Cartera", "Activo", client_name, product, total_balance, ""))

    contacts.sort(key=lambda row: (row.client_id, row.phone, row.origin, row.reference_name, row.relationship))
    metadata: dict[str, object] = {
        "active_clients": len(active_ids),
        "indexed_clients": len(active_ids | reference_ids),
        "reference_records": len(contacts),
        "updated_at": datetime.now(),
    }
    return contacts, metadata


def write_workbook(contacts: list[Contact], metadata: dict[str, object], destination: Path) -> None:
    workbook = Workbook()
    try:
        summary = workbook.active
        summary.title = "Resumen"
        summary.append(["Archivo CRM", CRM_FILENAME])
        summary.append(["Clientes activos", metadata["active_clients"]])
        summary.append(["Clientes indexados", metadata["indexed_clients"]])
        summary.append(["Registros consultables", metadata["reference_records"]])
        summary.append(["Generado", metadata["updated_at"].strftime("%d/%m/%Y %H:%M")])
        summary.column_dimensions["A"].width = 24
        summary.column_dimensions["B"].width = 28
        summary.sheet_view.showGridLines = False
        for cell in summary[1]:
            cell.font = Font(name="Arial", bold=True, color="FFFFFF")
            cell.fill = PatternFill("solid", fgColor="173B68")

        worksheet = workbook.create_sheet(RECORD_SHEET)
        worksheet.append(EXPORT_HEADERS)
        for row in contacts:
            worksheet.append([
                row.client_id, row.phone, row.reference_name, row.origin, row.status,
                row.client_name, row.product, row.total_balance, row.relationship,
            ])
        for cell in worksheet[1]:
            cell.fill = PatternFill("solid", fgColor="173B68")
            cell.font = Font(name="Arial", color="FFFFFF", bold=True)
            cell.alignment = Alignment(horizontal="center", vertical="center")
        worksheet.freeze_panes = "A2"
        worksheet.sheet_view.showGridLines = False
        widths = {"CLIENTE_UNICO": 18, "TELEFONO": 17, "ESTATUS": 14, "NOMBRE_REFERENCIA": 38, "ORIGEN": 16, "NOMBRE_CTE": 34, "PRODUCTO": 24, "SALDO_TOTAL": 18, "PARENTESCO": 22}
        for column, header in enumerate(EXPORT_HEADERS, start=1):
            worksheet.column_dimensions[worksheet.cell(row=1, column=column).column_letter].width = widths.get(header, 18)
        if contacts:
            last_column = worksheet.cell(row=1, column=len(EXPORT_HEADERS)).column_letter
            table = Table(displayName="RegistrosTable", ref=f"A1:{last_column}{len(contacts) + 1}")
            table.tableStyleInfo = TableStyleInfo(name="TableStyleMedium2", showFirstColumn=False, showLastColumn=False, showRowStripes=True, showColumnStripes=False)
            worksheet.add_table(table)

        destination.parent.mkdir(parents=True, exist_ok=True)
        with NamedTemporaryFile(dir=destination.parent, prefix=f".{destination.stem}-", suffix=".xlsx", delete=False) as temporary:
            temporary_path = Path(temporary.name)
        try:
            workbook.save(temporary_path)
            os.replace(temporary_path, destination)
        finally:
            temporary_path.unlink(missing_ok=True)
    finally:
        workbook.close()


def main() -> None:
    args = argument_parser()
    output = args.salida.resolve()
    if output in {args.referencias.resolve(), args.cartera.resolve()}:
        raise SystemExit("No se generó el CRM: la salida debe ser distinta de los dos archivos de entrada.")
    try:
        records, metadata = build_index(read_rows(args.referencias.resolve(), REFERENCE_FIELDS), read_rows(args.cartera.resolve(), CARTERA_FIELDS))
        write_workbook(records, metadata, output)
    except (FileNotFoundError, OSError, ValueError) as error:
        raise SystemExit(f"No se generó el CRM: {error}") from error
    print(f"Archivo creado: {output}")
    print(f"Clientes activos: {metadata['active_clients']:,}")
    print(f"Clientes indexados: {metadata['indexed_clients']:,}")
    print(f"Registros consultables: {metadata['reference_records']:,}")


if __name__ == "__main__":
    main()
