# ComfyUI workflows

ComfyUI workflows in API format, used through the AMW (`amw ki vorlage` / `amw ki auftrag`).
The AMW fills the placeholders `{prompt}`, `{negativ}`, `{seed}`, `{breite}` and `{hoehe}`.
Before running, it checks that every model file in the workflow is registered and released
(`frei`). GPU work runs only through the shared AMW queue.

| Workflow | Models (AMW names) | Notes |
|---|---|---|
| `workflows/z-image-turbo-t2i.api.json` | z-image-turbo (GGUF Q5_K_M), qwen3-4b (text encoder), z-image-vae | Text to image. 8 steps, CFG 1, `res_multistep`, AuraFlow shift 3 (as in the ComfyUI Z-Image-Turbo template). The negative prompt is zeroed (`ConditioningZeroOut`) because CFG 1 ignores it. Needs the ComfyUI-GGUF custom node. |

Prompts live in `prompts/` (versioned, English). Example for the S1 style check:

```sh
amw ki vorlage comfyui z-image-turbo-t2i --datei tools/comfyui/workflows/z-image-turbo-t2i.api.json
amw ki auftrag comfyui --vorlage z-image-turbo-t2i --prompt "<scene> <style>" --seed 20261109 --anzahl 2 --breite 1344 --hoehe 768
```
