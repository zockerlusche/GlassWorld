# Installing GlassWorld

This guide is for the first release. The game always runs, even without AI: without the language
AI the inhabitants live by simple rules; without the image AI the world shows simple pictures;
there is no sound AI in this version, the world uses its basic sounds.

## What you need

- Windows 10 or 11 (64-bit)
- an NVIDIA graphics card with at least 8 GB of graphics memory (for the language AI)
- about 12 GB of free disk space; with the image AI about 11 GB more (tier standard) or 33 GB
  more (comfort, with the image editing model)
- for the image AI also: your own installation of [ComfyUI](https://github.com/Comfy-Org/ComfyUI)
  with the extension ComfyUI-GGUF (see below)

Which AI runs on your computer depends on the hardware tier (README, "Hardware tiers").

## Step by step

1. Download the release archive from the GitHub releases page and unpack it into a folder of your
   choice. Nothing is installed system-wide.
2. Start `GlassWorld.exe`.
3. **First start:** the game shows what is still missing, with name, size, licence and purpose,
   and asks you before anything is downloaded:
   - **Ollama** (MIT), the runtime for the language AI. If you already have a suitable Ollama, the
     game uses it (and starts it only with your consent).
   - **The language model** `qwen3.5:9b` (Apache-2.0, about 6.6 GB).

   Downloads come only from the official sources, in a fixed, tested version with a checksum. You
   can cancel at any time; the next start continues where it stopped.
4. **Image AI (tiers standard and comfort):**
   1. Install ComfyUI yourself (it is not part of the game and is licensed GPL-3.0).
   2. Install the extension **ComfyUI-GGUF** in ComfyUI: in the ComfyUI Manager, open "Custom
      Nodes Manager", search for "ComfyUI-GGUF", install it and restart ComfyUI.
   3. Start ComfyUI and keep it running in the background.
   4. Name the folder of your ComfyUI installation in the game's setup window. After your consent
      the game downloads the image models (fixed versions with checksums) into its models
      folder. Files of the same name are never overwritten without asking.
5. At start the game checks each AI and calmly shows whether everything is ready or what is
   missing.

## When something does not work

The self-check at start names the cause. Here is what you can do.

### Language AI

| Message | What you can do |
|---|---|
| Your computer is too weak for the language AI. | A graphics card with at least 8 GB of graphics memory. Until then the inhabitants live by simple rules. |
| Ollama is not installed yet. | Download Ollama in the setup window. |
| Your Ollama is too old. | Update Ollama, or close it: then the game uses its own. |
| Ollama is installed but not running. | Allow the game to start Ollama in the setup window. |
| Ollama could not be set up. | Start the download again. If that does not help: check your virus scanner (it must not block the game's folder) and create a diagnostics report. |
| The language model is missing. | Download it in the setup window. |
| Your Ollama holds another version. | Allow the tested version to be downloaded. Your own version under another name stays untouched. |
| The language model could not be downloaded. | Start the download again; it continues. Enough free space? |
| The language model could not start. | Close other programs that use a lot of graphics memory (games, video editing, another ComfyUI image). |

### Image AI

| Message | What you can do |
|---|---|
| The image AI needs your own ComfyUI installation. | Install ComfyUI (step 4) and name its folder in the setup window. |
| ComfyUI does not answer. | Start ComfyUI and keep it running. It has to run on this computer (default: `http://127.0.0.1:8188`). |
| An extension is missing in your ComfyUI. | Install the named extension (usually ComfyUI-GGUF) with the ComfyUI Manager and restart ComfyUI. |
| The image models are missing. | Download them in the setup window. |
| The image models could not be downloaded. | Start again; it continues. Enough space in the ComfyUI folder? |

While the image AI is missing, the world shows simple pictures. The game does not crash and does
not wait.

### Diagnostics report

If none of this helps, create a **diagnostics report** in the game (button in the setup window).
It contains versions, the self-check, the hardware tier, the state of the models and the last
errors. It contains **no** credentials and **no** paths with your user name. It is stored in
`%APPDATA%\Godot\app_userdata\GlassWorld\ai-service\logs\` and stays on your computer until you
pass it on yourself, for example in a GitHub issue.
