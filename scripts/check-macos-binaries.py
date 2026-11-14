#!/usr/bin/env python3
"""Проверка архитектуры и нижней границы macOS у всех Mach-O в готовом комплекте."""

import struct
import sys
from pathlib import Path


ARCHITECTURES = {"osx-x64": 0x01000007, "osx-arm64": 0x0100000C}
THIN = {b"\xce\xfa\xed\xfe": ("<", 28), b"\xcf\xfa\xed\xfe": ("<", 32),
        b"\xfe\xed\xfa\xce": (">", 28), b"\xfe\xed\xfa\xcf": (">", 32)}
FAT = {b"\xca\xfe\xba\xbe": (">", False), b"\xbe\xba\xfe\xca": ("<", False),
       b"\xca\xfe\xba\xbf": (">", True), b"\xbf\xba\xfe\xca": ("<", True)}


def version(text):
    parts = tuple(int(part) for part in text.split("."))
    if not 1 <= len(parts) <= 3 or any(part < 0 or part > 65535 for part in parts):
        raise ValueError("неверная версия macOS")
    return (parts + (0, 0))[:3]


def packed_version(value):
    return value >> 16, (value >> 8) & 255, value & 255


def minimum(data, architecture):
    """Не Mach-O -> None; Mach-O без нужного среза/границы -> отказ, а не пропуск."""
    magic = data[:4]
    if magic in FAT:
        endian, wide = FAT[magic]
        count, = struct.unpack_from(endian + "I", data, 4)
        size = 32 if wide else 20
        if count > 64 or 8 + count * size > len(data):
            raise ValueError("поврежденный универсальный Mach-O")
        for index in range(count):
            position = 8 + index * size
            cpu, = struct.unpack_from(endian + "I", data, position)
            offset, length = struct.unpack_from(endian + ("QQ" if wide else "II"), data, position + 8)
            if cpu == architecture:
                if offset + length > len(data):
                    raise ValueError("срез Mach-O выходит за границы файла")
                result = minimum(data[offset:offset + length], architecture)
                if result is None:
                    raise ValueError("вместо среза Mach-O записаны другие данные")
                return result
        raise ValueError("нет нужной архитектуры в универсальном Mach-O")
    if magic not in THIN:
        return None
    endian, header = THIN[magic]
    cpu, = struct.unpack_from(endian + "I", data, 4)
    if cpu != architecture:
        raise ValueError("Mach-O собран для другого процессора")
    count, commands_size = struct.unpack_from(endian + "II", data, 16)
    end = header + commands_size
    if end > len(data) or count > commands_size // 8:
        raise ValueError("поврежденная таблица команд Mach-O")
    position = header
    versions = []
    for _ in range(count):
        command, size = struct.unpack_from(endian + "II", data, position)
        if size < 8 or position + size > end:
            raise ValueError("поврежденная команда Mach-O")
        if command == 0x32:  # LC_BUILD_VERSION
            if size < 24:
                raise ValueError("поврежденная команда LC_BUILD_VERSION")
            platform, value = struct.unpack_from(endian + "II", data, position + 8)
            if platform != 1:
                raise ValueError("Mach-O собран для другой платформы Apple")
            versions.append(packed_version(value))
        elif command == 0x24:  # LC_VERSION_MIN_MACOSX
            if size < 16:
                raise ValueError("поврежденная команда LC_VERSION_MIN_MACOSX")
            value, = struct.unpack_from(endian + "I", data, position + 8)
            versions.append(packed_version(value))
        position += size
    if not versions:
        raise ValueError("Mach-O не указывает минимальную macOS")
    return max(versions)


def check(folder, rid, target):
    architecture = ARCHITECTURES[rid]
    checked = 0
    for path in sorted(Path(folder).rglob("*")):
        if not path.is_file():
            continue
        with path.open("rb") as stream:
            if stream.read(4) not in THIN.keys() | FAT.keys():
                continue
        try:
            required = minimum(path.read_bytes(), architecture)
            if required is None or required > target:
                raise ValueError("минимальная macOS " + ".".join(map(str, required or ())) + " новее заявленной")
        except (ValueError, struct.error) as error:
            raise ValueError(str(path.relative_to(folder)) + ": " + str(error)) from error
        checked += 1
    if checked == 0:
        raise ValueError("в комплекте нет Mach-O: проверка не выполнена")
    print(f"Mach-O: проверено {checked}, архитектура {rid}, нижняя граница macOS {'.'.join(map(str, target))}")


if __name__ == "__main__":
    try:
        if len(sys.argv) != 4:
            raise ValueError("нужны папка комплекта, osx-x64/osx-arm64 и минимальная macOS")
        check(Path(sys.argv[1]), sys.argv[2], version(sys.argv[3]))
    except (ValueError, KeyError, OSError) as error:
        raise SystemExit("Совместимость macOS не подтверждена: " + str(error)) from error
