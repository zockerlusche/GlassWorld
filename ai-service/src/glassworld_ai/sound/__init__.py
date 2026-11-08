"""Sound jobs (B2530, D471 D7): make a short sound for a thing in the computing night.

The local sound model (MOSS-SoundEffect, Apache-2.0) runs in its own process and environment
(torch, CUDA) and is loaded and unloaded through the residency (B7). Every result is checked
(length, level, silence) and retried with the next seed; if nothing passes, the job fails and the
core falls back to the base library. A deterministic mock stands in without the model.
"""

from glassworld_ai.sound.check import SoundStats, measure_wav
from glassworld_ai.sound.handler import (
    GenerationSettings,
    MockSound,
    MossSound,
    load_generation_settings,
)
from glassworld_ai.sound.worker import MossWorker

__all__ = [
    "GenerationSettings",
    "MockSound",
    "MossSound",
    "MossWorker",
    "SoundStats",
    "load_generation_settings",
    "measure_wav",
]
