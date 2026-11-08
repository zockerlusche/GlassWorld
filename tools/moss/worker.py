"""MOSS-SoundEffect worker for the AI service (B2530, D471 D7).

Runs in the MOSS environment (torch cu128, official code at a pinned commit), started and ended by
the service (glassworld_ai.sound.worker). Loads the pipeline once (DiT in bfloat16, official VRAM
management; measurement report D440), prints READY, then renders one JSON request per stdin line.
With --resident everything is built straight on the GPU and the weights are streamed tensor
by tensor from the safetensors files, without CPU offload (TL #19563): the process then needs
about 7 GB of RAM instead of ~12 GB, while the whole model (~7.8 GB in bfloat16) stays in VRAM.
The peak working set goes to stderr ("PEAK_RSS_GB ...") after loading and after each request.
--resident is for development and batch runs only (VRAM peak ~10 GB is above the service budget;
the AI service would need its own footprint entry first, review GW2 #117).
Requests:

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


def peak_rss_gb() -> float:
    """Peak working set of this process in GB (Windows; 0 elsewhere)."""
    if sys.platform != "win32":
        return 0.0
    import ctypes
    from ctypes import wintypes

    names = (
        "PeakWorkingSetSize",
        "WorkingSetSize",
        "QuotaPeakPagedPoolUsage",
        "QuotaPagedPoolUsage",
        "QuotaPeakNonPagedPoolUsage",
        "QuotaNonPagedPoolUsage",
        "PagefileUsage",
        "PeakPagefileUsage",
    )

    class Counters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
            (n, ctypes.c_size_t) for n in names
        ]

    counters = Counters()
    counters.cb = ctypes.sizeof(Counters)
    kernel32 = ctypes.WinDLL("kernel32")
    psapi = ctypes.WinDLL("psapi")
    kernel32.GetCurrentProcess.restype = wintypes.HANDLE
    psapi.GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
    psapi.GetProcessMemoryInfo(kernel32.GetCurrentProcess(), ctypes.byref(counters), counters.cb)
    return counters.PeakWorkingSetSize / 1e9


def load_resident(model_dir: str):
    """Builds the pipeline on the GPU with a small RAM peak (see module doc)."""
    import torch
    from moss_soundeffect_v2 import MossSoundEffectPipeline
    from moss_soundeffect_v2.diffsynth.models.qwen3_text_encoder import Qwen3TextEncoder
    from moss_soundeffect_v2.diffsynth.models.wan_audio_dit import WanAudioModel
    from moss_soundeffect_v2.diffsynth.pipelines import wan_audio
    from safetensors import safe_open
    from transformers import AutoConfig, AutoModelForCausalLM

    root = Path(model_dir)
    dtype, device = torch.bfloat16, "cuda"
    index = json.loads((root / "model_index.json").read_text(encoding="utf-8"))
    sched = json.loads((root / "scheduler" / "scheduler_config.json").read_text(encoding="utf-8"))
    cfg = json.loads((root / "transformer" / "config.json").read_text(encoding="utf-8"))

    def stream(module, files, rename=lambda k: k) -> list[str]:
        """Copies each tensor of the files into the module, one at a time (small RAM peak)."""
        target = module.state_dict()
        missing = set(target)
        for file in files:
            with safe_open(str(file), framework="pt", device="cpu") as f:
                for key in f.keys():  # noqa: SIM118 - safe_open has no __contains__/__iter__
                    name = rename(key)
                    if name in target:
                        with torch.no_grad():
                            target[name].copy_(f.get_tensor(key).to(target[name].dtype))
                        missing.discard(name)
        return sorted(missing)

    # DiT first (largest): built on the GPU in bfloat16 (parameters only, like the official setup;
    # the
    # RoPE buffers keep their own type), weights streamed and renamed like the official loader.
    with torch.device(device):
        dit = WanAudioModel(
            in_dim=cfg["in_dim"],
            out_dim=cfg["out_dim"],
            text_dim=cfg["text_dim"],
            freq_dim=cfg["freq_dim"],
            eps=cfg["eps"],
            patch_size=tuple(cfg["patch_size"]),
            has_image_input=cfg["has_image_input"],
            dim=cfg["dim"],
            ffn_dim=cfg["ffn_dim"],
            num_heads=cfg["num_heads"],
            num_layers=cfg["num_layers"],
            vae_type=cfg.get("vae_type", "dac"),
        )
    for parameter in dit.parameters():
        parameter.data = parameter.data.to(dtype)
    torch.cuda.empty_cache()

    def dit_name(key: str) -> str:
        return next(iter(wan_audio._convert_hf_dit_state_dict({key: None})))

    missing = [
        m
        for m in stream(
            dit, [root / "transformer" / "diffusion_pytorch_model.safetensors"], dit_name
        )
        if "freqs" not in m
    ]
    if missing:
        raise RuntimeError(f"DiT weights missing: {missing[:5]}")
    dit.eval()

    # Text encoder (Qwen3): from its config on the GPU in bfloat16, weights streamed from shards.
    te_dir = root / "text_encoder"
    config = AutoConfig.from_pretrained(str(te_dir), output_hidden_states=True)
    with torch.device(device):
        qwen = AutoModelForCausalLM.from_config(config, torch_dtype=dtype)
    missing = stream(qwen, sorted(te_dir.glob("*.safetensors")))
    if getattr(config, "tie_word_embeddings", False):
        qwen.tie_weights()
        missing = [m for m in missing if not m.startswith("lm_head")]
    if missing:
        raise RuntimeError(f"text encoder weights missing: {missing[:5]}")
    qwen.eval()
    for parameter in qwen.parameters():
        parameter.requires_grad = False
    text_encoder = Qwen3TextEncoder.__new__(Qwen3TextEncoder)
    torch.nn.Module.__init__(text_encoder)
    text_encoder.model = qwen
    text_encoder.dim = qwen.config.hidden_size

    prompter = wan_audio.WanPrompter(tokenizer_path=str(root / "tokenizer"))
    prompter.fetch_models(text_encoder)
    vae = wan_audio.DAC.load(str(root / "vae" / "vae_128d_48k.pth")).to(device)

    engine = wan_audio.WanAudioPipeline(
        device=device, torch_dtype=dtype, flow_shift=sched.get("shift", 5.0)
    )
    engine.text_encoder, engine.prompter, engine.vae, engine.dit = text_encoder, prompter, vae, dit
    engine.audio_latent_dim = cfg["in_dim"]
    engine.num_samples_division_factor = vae.hop_length
    engine.dit_variant = index.get("dit_variant")
    return MossSoundEffectPipeline(
        engine=engine,
        sample_rate=int(index.get("sample_rate", 48000)),
        max_inference_seconds=int(index.get("max_inference_seconds", 30)),
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--code-dir", required=True)
    parser.add_argument(
        "--resident", action="store_true", help="whole model on the GPU, small RAM peak"
    )
    parser.add_argument("--load-only", action="store_true", help="load, report the peak RAM, exit")
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

    if args.resident:
        pipe = load_resident(args.model_dir)
    else:
        pipe = MossSoundEffectPipeline.from_pretrained(
            args.model_dir, torch_dtype=torch.bfloat16, device="cuda"
        )
        for parameter in pipe.engine.dit.parameters():
            parameter.data = parameter.data.to(torch.bfloat16)
        pipe.engine.enable_cpu_offload()
        pipe.engine.load_models_to_device([])
    torch.cuda.empty_cache()
    print(
        f"PEAK_RSS_GB {peak_rss_gb():.2f} VRAM_GB {torch.cuda.memory_allocated() / 1e9:.2f}",
        flush=True,
    )
    if args.load_only:
        return 0
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
            vram = torch.cuda.max_memory_allocated() / 1e9
            print(f"PEAK_RSS_GB {peak_rss_gb():.2f} VRAM_PEAK_GB {vram:.2f}", flush=True)
            answer(json.dumps({"ok": True}))
        except Exception as error:  # one bad request must not end the worker
            answer(json.dumps({"ok": False, "error": f"{type(error).__name__}: {error}"[:300]}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
