"""Measurement run for MOSS-SoundEffect v2.0 (AMW A1353, B8 R8, input for the compute night B7).

Runs outside the uv workspace in the MOSS virtual environment (torch cu128, official code at a
pinned commit) and only through the AMW GPU queue (KR5):

    amw lauf ausfuehren --gpu --schwer --aufgabe 1353 --ergebnis <out>/result.json -- \
        <venv>/python tools/moss/probe.py \
        --model-dir <weights> --code-dir <MOSS-TTS> --out-dir <out>

Measures model load time, generation time per clip, peak VRAM (PyTorch allocator and the whole
process via nvidia-smi), and checks every WAV (length, peak, not silent). Writes result.json.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

PROMPTS = [
    ("chop_wood", "A stone axe chopping dry wood, single strikes, outdoors, calm.", 3.0),
    ("bowl_water", "Pouring water into a wooden bowl, close, quiet.", 3.0),
    ("wind_chime", "A small handmade wooden wind chime clacking softly in a light breeze.", 10.0),
]


def gpu_used_mib() -> int | None:
    """Memory used on the GPU by all processes (nvidia-smi), or None if unavailable."""
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            check=True,
            timeout=20,
        ).stdout
        return int(out.strip().splitlines()[0])
    except (OSError, subprocess.SubprocessError, ValueError, IndexError):
        return None


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument(
        "--code-dir",
        required=True,
        help="checkout of OpenMOSS/MOSS-TTS (contains moss_soundeffect_v2)",
    )
    parser.add_argument("--out-dir", required=True)
    parser.add_argument("--steps", type=int, default=100)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument(
        "--dit-dtype",
        choices=["float32", "bfloat16"],
        default="bfloat16",
        help="the official loader keeps the DiT in float32 (5.2 GB); bfloat16 halves it",
    )
    parser.add_argument("--clips", type=int, default=3, help="how many of the test clips to render")
    parser.add_argument(
        "--compile",
        action="store_true",
        help="allow torch.compile/Triton (default off: TORCHDYNAMO_DISABLE=1)",
    )
    args = parser.parse_args()

    if not args.compile:
        os.environ["TORCHDYNAMO_DISABLE"] = "1"
    sys.path.insert(0, args.code_dir)

    import numpy as np
    import soundfile as sf
    import torch
    from moss_soundeffect_v2 import MossSoundEffectPipeline

    out = Path(args.out_dir)
    out.mkdir(parents=True, exist_ok=True)
    result: dict[str, object] = {
        "torch": torch.__version__,
        "cuda": torch.version.cuda,
        "gpu": torch.cuda.get_device_name(0),
        "compile": args.compile,
        "steps": args.steps,
        "gpu_used_before_mib": gpu_used_mib(),
    }

    torch.cuda.reset_peak_memory_stats()
    start = time.perf_counter()
    pipe = MossSoundEffectPipeline.from_pretrained(
        args.model_dir, torch_dtype=torch.bfloat16, device="cuda"
    )
    torch.cuda.synchronize()
    if args.dit_dtype == "bfloat16":
        # Parameters only: the DiT keeps complex rotary buffers (freqs) that must stay complex.
        for parameter in pipe.engine.dit.parameters():
            parameter.data = parameter.data.to(torch.bfloat16)
        torch.cuda.empty_cache()
    result["dit_dtype"] = str(next(pipe.engine.dit.parameters()).dtype)
    result["load_seconds"] = round(time.perf_counter() - start, 2)
    free, total = torch.cuda.mem_get_info()
    result["device_free_after_load_mib"] = round(free / 2**20)
    result["device_total_mib"] = round(total / 2**20)
    result["vram_after_load_mib"] = round(torch.cuda.memory_allocated() / 2**20)
    result["gpu_used_after_load_mib"] = gpu_used_mib()

    clips = []
    process_peak = result["gpu_used_after_load_mib"] or 0
    for name, prompt, seconds in PROMPTS[: args.clips]:
        torch.cuda.reset_peak_memory_stats()
        start = time.perf_counter()
        audio = pipe(prompt=prompt, seconds=seconds, num_inference_steps=args.steps, seed=args.seed)
        torch.cuda.synchronize()
        took = time.perf_counter() - start
        used = gpu_used_mib() or 0
        process_peak = max(process_peak, used)
        samples = audio[0].detach().float().cpu().numpy()
        mono = samples.mean(axis=0) if samples.ndim == 2 else samples
        path = out / f"{name}.wav"
        sf.write(path, mono.astype(np.float32), pipe.sample_rate)
        peak = float(np.abs(mono).max())
        rms = float(np.sqrt(np.mean(mono**2)))
        clips.append(
            {
                "name": name,
                "prompt": prompt,
                "seconds": seconds,
                "file": path.name,
                "generate_seconds": round(took, 2),
                "seconds_per_step": round(took / args.steps, 3),
                "peak_allocated_mib": round(torch.cuda.max_memory_allocated() / 2**20),
                "peak_reserved_mib": round(torch.cuda.max_memory_reserved() / 2**20),
                "gpu_used_after_mib": used,
                "duration_s": round(len(mono) / pipe.sample_rate, 3),
                "sample_rate": pipe.sample_rate,
                "peak": round(peak, 4),
                "rms": round(rms, 4),
                "ok": abs(len(mono) / pipe.sample_rate - seconds) < 0.1 and peak > 0.01,
            }
        )
        print(json.dumps(clips[-1]), flush=True)

    result["clips"] = clips
    result["gpu_used_peak_mib"] = process_peak
    result["ok"] = all(c["ok"] for c in clips)
    (out / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print("PROBE_OK" if result["ok"] else "PROBE_FAIL", flush=True)
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
