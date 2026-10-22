import base64
from xml.etree import ElementTree as ET

import pytest
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

from custom_components.ma_ew85s_local.protocol import PurifierError
from custom_components.ma_ew85s_local.protocol.codec import KEY, decrypt, encrypt, parse_xml


@pytest.mark.parametrize("text", ["", "1234567890123456", "<CSV></CSV>", "<CSV>µ 日本語</CSV>"])
def test_random_iv_roundtrip_and_padding(text):
    first = encrypt(text)
    assert decrypt(first) == text
    assert first != encrypt(text)
    data = base64.b64decode(ET.fromstring(first).text)
    decoder = Cipher(algorithms.AES(KEY), modes.CBC(data[:16])).decryptor()
    padded = decoder.update(data[16:]) + decoder.finalize()
    raw = text.encode()
    assert padded == raw + b"\x80" + b"\x00" * (15 - len(raw) % 16)
    assert len(padded) == len(raw) + 16 - len(raw) % 16


@pytest.mark.parametrize("xml", ["<LSV/>", "<ESV>AA==</ESV>", "<ESV><child/></ESV>",
    "<ESV>!</ESV>", "<ESV>" + base64.b64encode(bytes(33)).decode() + "</ESV>",
    "<ESV>", "<!DOCTYPE ESV [<!ENTITY test 'x'>]><ESV>&test;</ESV>", "<ESV>" + "x" * 65536 + "</ESV>"])
def test_rejects_bad_envelopes(xml):
    with pytest.raises(PurifierError):
        decrypt(xml)


@pytest.mark.parametrize("padded", [bytes(16), b"x" * 16, b"\x80" + bytes(31), b"\xff\x80" + bytes(14)])
def test_rejects_padding_and_invalid_utf8(padded):
    iv = bytes(16)
    encoder = Cipher(algorithms.AES(KEY), modes.CBC(iv)).encryptor()
    envelope = "<ESV>" + base64.b64encode(iv + encoder.update(padded) + encoder.finalize()).decode() + "</ESV>"
    with pytest.raises(PurifierError):
        decrypt(envelope)


@pytest.mark.parametrize("xml", ["<!DOCTYPE LSV><LSV/>", "<!ENTITY x 'y'><LSV/>", "x" * 65537])
def test_xml_limits(xml):
    with pytest.raises(PurifierError):
        parse_xml(xml)
