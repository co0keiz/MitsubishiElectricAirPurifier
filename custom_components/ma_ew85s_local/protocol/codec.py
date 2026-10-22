"""Bounded encrypted XML transport, independent of Home Assistant."""
import base64
import binascii
import os
from xml.etree import ElementTree as ET

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

from .errors import protocol_error

KEY = b"unregistered\x00\x00\x00\x00"
MAX_XML_LENGTH = 65536


def parse_xml(xml: str) -> ET.Element:
    if len(xml) > MAX_XML_LENGTH or "<!DOCTYPE" in xml.upper() or "<!ENTITY" in xml.upper():
        raise protocol_error("Oversized XML or prohibited DTD/entity declaration.")
    try:
        return ET.fromstring(xml)
    except (ET.ParseError, ValueError) as err:
        raise protocol_error("Invalid XML response.") from err


def encrypt(plaintext: str) -> str:
    data = plaintext.encode("utf-8")
    padding_length = 16 - len(data) % 16
    padded = data + b"\x80" + b"\x00" * (padding_length - 1)
    iv = os.urandom(16)
    encryptor = Cipher(algorithms.AES(KEY), modes.CBC(iv)).encryptor()
    encoded = base64.b64encode(iv + encryptor.update(padded) + encryptor.finalize()).decode("ascii")
    return f'<?xml version="1.0" encoding="UTF-8"?><ESV>{encoded}</ESV>'


def decrypt(envelope: str) -> str:
    root = parse_xml(envelope)
    if root.tag != "ESV" or len(root):
        raise protocol_error("Expected an ESV envelope without children.")
    try:
        data = base64.b64decode((root.text or "").strip(), validate=True)
        if len(data) < 32 or (len(data) - 16) % 16:
            raise protocol_error("Invalid encrypted response length.")
        decryptor = Cipher(algorithms.AES(KEY), modes.CBC(data[:16])).decryptor()
        padded = decryptor.update(data[16:]) + decryptor.finalize()
        unpadded = padded.rstrip(b"\x00")
        if not unpadded.endswith(b"\x80") or len(padded) - len(unpadded) >= 16:
            raise protocol_error("Invalid ISO/IEC 7816-4 padding.")
        return unpadded[:-1].decode("utf-8", errors="strict")
    except (binascii.Error, ValueError, UnicodeError) as err:
        raise protocol_error("Invalid encrypted response.") from err
