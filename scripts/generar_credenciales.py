#!/usr/bin/env python3
"""Genera el JSON de hashes que consume ASCENDRA_AGENT_CREDENTIALS_JSON.

El resultado se imprime únicamente en pantalla para copiarlo en Render. Nunca
guarda contraseñas ni el JSON generado en el repositorio.
"""

from __future__ import annotations

import argparse
import getpass
import hashlib
import json
import secrets
import unicodedata
from pathlib import Path

from openpyxl import load_workbook


PROJECT_DIR = Path(__file__).resolve().parents[1]


def key(value: object) -> str:
    text = unicodedata.normalize("NFKD", str(value or ""))
    return "".join(character for character in text if not unicodedata.combining(character)).strip().casefold()


def active_users(path: Path) -> list[str]:
    workbook = load_workbook(path, read_only=True, data_only=True)
    try:
        worksheet = workbook.active
        headers = next(worksheet.iter_rows(min_row=1, max_row=1, values_only=True), ())
        columns = {key(header): index for index, header in enumerate(headers)}
        if "usuario" not in columns or "estado" not in columns:
            raise ValueError("El padrón debe incluir las columnas usuario y estado.")
        users: list[str] = []
        for row in worksheet.iter_rows(min_row=2, values_only=True):
            username = str(row[columns["usuario"]] or "").strip().casefold()
            status = key(row[columns["estado"]])
            if username and status in {"activo", "active", "1", "true", "si"}:
                users.append(username)
        return sorted(set(users))
    finally:
        workbook.close()


def credential_hash(password: str) -> str:
    salt = secrets.token_urlsafe(16)
    # OpenSSL puede imponer un límite de 32 MiB por defecto; esta combinación
    # necesita un poco más y debe coincidir con el verificador .NET.
    digest = hashlib.scrypt(password.encode("utf-8"), salt=salt.encode("utf-8"), n=32768, r=8, p=1, dklen=64, maxmem=64 * 1024 * 1024)
    return f"scrypt:32768:8:1${salt}${digest.hex()}"


def main() -> None:
    parser = argparse.ArgumentParser(description="Genera hashes de acceso para Ascendra Agentes.")
    parser.add_argument("--usuario", action="append", default=[], help="Usuario a configurar; puede repetirse.")
    parser.add_argument("--todos-activos", action="store_true", help="Solicita una contraseña para cada usuario activo del padrón.")
    parser.add_argument("--padron", type=Path, default=PROJECT_DIR / "data/input/usuarios_ascendra.xlsx")
    args = parser.parse_args()

    users = [value.strip().casefold() for value in args.usuario if value.strip()]
    if args.todos_activos:
        users.extend(active_users(args.padron))
    users = sorted(set(users))
    if not users:
        raise SystemExit("Indica al menos --usuario NOMBRE o usa --todos-activos.")

    credentials: dict[str, dict[str, str]] = {}
    for username in users:
        password = getpass.getpass(f"Contraseña para {username}: ")
        confirmation = getpass.getpass("Confirma la contraseña: ")
        if not password:
            raise SystemExit("La contraseña no puede estar vacía.")
        if password != confirmation:
            raise SystemExit(f"Las contraseñas no coinciden para {username}.")
        credentials[username] = {"password_hash": credential_hash(password)}

    print(json.dumps(credentials, ensure_ascii=False, separators=(",", ":")))


if __name__ == "__main__":
    main()
