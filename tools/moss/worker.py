"""MOSS-SoundEffect worker for the AI service (B2530, D471 D7).

Runs in the MOSS environment (torch cu128, official code at a pinned commit), started and ended by
the service (glassworld_ai.sound.worker). Loads the pipeline once (DiT in bfloat16, official VRAM
management; measurement report D440), prints READY, then renders one JSON request per stdin line:

    {"prompt": "...", "seconds": 2.0, "loop": false, "steps": 50, "seed": 77, "out": "x/snd.wav"}

and answers with one JSON line {"ok": true} or {"ok": false, "error": "..."}. Loops get a crossfaded
seam so they repeat without a click. Writes 16-bit PCM mono; the service checks the file.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

LOOP_FADE_S = 1.0  # crossfade of a loop seam (technical, like the probe's make_base)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--code-dir", required=True)
    args = parser.parse_args()
    os.environ.setdefault("TORCHDYNAMO_DISABLE", "1")
    sys.path.insert(0, args.code_dir)

    # The protocol has stdout to itself: the model code prints while loading ("Loading from ..."),
    # so everything else goes to stderr (found in the first live run).
    protocol = sys.stdout
    sys.stdout = sys.stderr

    def answer(text: str) -> None:
        protocol.write(text + "\n")
        protocol.flush()

    import numpy as np
    import soundfile as sf
    import torch
    from moss_soundeffect_v2 import MossSoundEffectPipeline

    pipe = MossSoundEffectPipeline.from_pretrained(
        args.model_dir, torch_dtype=torch.bfloat16, device="cuda"
    )
    for parameter in pipe.engine.dit.parameters():
        parameter.data = parameter.data.to(torch.bfloat16)
    pipe.engine.enable_cpu_offload()
    pipe.engine.load_models_to_device([])
    torch.cuda.empty_cache()
    answer("READY")

    for line in sys.stdin:
        try:
            request = json.loads(line)
            loop = bool(request["loop"])
            seconds = float(request["seconds"])
            render = seconds + (LOOP_FADE_S if loop else 0.0)
            audio = pipe(
                prompt=str(request["prompt"]),
                seconds=render,
                num_inference_steps=int(request["steps"]),
                seed=int(request["seed"]),
            )
            samples = audio[0].detach().float().cpu().numpy()
            mono = samples.mean(axis=0) if samples.ndim == 2 else samples
            if loop:
                n = int(pipe.sample_rate * LOOP_FADE_S)
                body = mono[:-n].copy()
                t = np.linspace(0, np.pi / 2, n)
                body[:n] = mono[:n] * np.sin(t) + mono[-n:] * np.cos(t)
                mono = body
            out = Path(str(request["out"]))
            out.parent.mkdir(parents=True, exist_ok=True)
            sf.write(
                out, np.clip(mono, -1.0, 1.0).astype(np.float32), pipe.sample_rate, subtype="PCM_16"
            )
            answer(json.dumps({"ok": True}))
        except Exception as error:  # one bad request must not end the worker
            answer(json.dumps({"ok": False, "error": f"{type(error).__name__}: {error}"[:300]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
