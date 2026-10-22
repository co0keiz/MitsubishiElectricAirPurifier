"""Protocol errors shared by all consumers."""
from enum import StrEnum


class PurifierErrorCode(StrEnum):
    CONNECTION_FAILED = "connection_failed"
    REQUEST_TIMED_OUT = "request_timed_out"
    PROTOCOL_ERROR = "protocol_error"
    IDENTITY_MISMATCH = "identity_mismatch"
    UNSUPPORTED_FIRMWARE = "unsupported_firmware"
    DEVICE_OFF = "device_off"
    COMMAND_UNCONFIRMED = "command_unconfirmed"


class PurifierError(Exception):
    def __init__(self, code: PurifierErrorCode, message: str, *,
                 command_may_have_succeeded: bool = False):
        super().__init__(message)
        self.code = code
        self.command_may_have_succeeded = command_may_have_succeeded


def protocol_error(message: str) -> PurifierError:
    return PurifierError(PurifierErrorCode.PROTOCOL_ERROR, message)
