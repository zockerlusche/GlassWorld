"""Measure a WAV file with the standard library: length, peak and gated loudness (B8, D440)."""

from __future__ import annotations

import array
import math
import wave
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class SoundStats:
    """What the check needs from a sound file."""

    seconds: float
    peak: float
    rms: float
    peak_dbfs: float
    loudness_dbfs: float


def _dbfs(value: float) -> float:
    return -120.0 if value <= 0 else max(-120.0, 20 * math.log10(value))


def measure_wav(path: Path, gate_dbfs: float = -50.0) -> SoundStats:
    """Reads 16-bit PCM (mono or stereo, mixed) and measures it.

    The loudness is the mean power of the 50 ms frames above the gate (silence before and after a
    sound does not lower it), the same rule as the base library check in the game.
    """
    with wave.open(str(path), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError(f"{path.name}: only 16-bit PCM is supported")
        channels = wav.getnchannels()
        rate = wav.getframerate()
        frames = wav.getnframes()
        raw = wav.readframes(frames)
    samples = array.array("h")
    samples.frombytes(raw)
    mono = [
        sum(samples[i + c] for c in range(channels)) / (channels * 32768.0)
        for i in range(0, len(samples), channels)
    ]
    peak = max((abs(s) for s in mono), default=0.0)
    power_all = sum(s * s for s in mono) / len(mono) if mono else 0.0
    frame = max(1, rate // 20)
    gate = 10 ** (gate_dbfs / 10)
    powers: list[float] = []
    for start in range(0, len(mono), frame):
        chunk = mono[start : start + frame]
        power = sum(s * s for s in chunk) / len(chunk)
        if power > gate:
            powers.append(power)
    loud = 10 * math.log10(sum(powers) / len(powers)) if powers else -120.0
    return SoundStats(
        seconds=len(mono) / rate if rate else 0.0,
        peak=peak,
        rms=math.sqrt(power_all),
        peak_dbfs=_dbfs(peak),
        loudness_dbfs=max(-120.0, loud),
    )
