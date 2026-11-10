# GlassWorld installieren

Diese Anleitung gilt für das erste Release. Das Spiel läuft immer, auch ohne KI: Fehlt die
Sprach-KI, leben die Bewohner nach einfachen Regeln; fehlt die Bild-KI, zeigt die Welt einfache
Bilder; Klang-KI gibt es in dieser Version nicht, die Welt nutzt ihre Grundklänge.

## Was du brauchst

- Windows 10 oder 11 (64 Bit)
- eine NVIDIA-Grafikkarte mit mindestens 8 GB Grafikspeicher (für die Sprach-KI)
- etwa 12 GB freien Platz; mit Bild-KI etwa 11 GB mehr (Stufe standard) bzw. 33 GB mehr (comfort,
  mit dem Bild-Bearbeitungsmodell)
- für die Bild-KI zusätzlich: eine eigene Installation von [ComfyUI](https://github.com/Comfy-Org/ComfyUI)
  mit der Erweiterung ComfyUI-GGUF (siehe unten)

Welche KI auf deinem Rechner läuft, hängt von der Hardware-Stufe ab (README, „Hardware tiers“).

## Schritt für Schritt

1. Lade das Release-Archiv von der GitHub-Release-Seite und entpacke es in einen Ordner deiner
   Wahl. Es wird nichts systemweit installiert.
2. Starte `GlassWorld.exe`.
3. **Erster Start:** Das Spiel zeigt, was noch fehlt, mit Name, Größe, Lizenz und Zweck, und
   fragt dich, bevor etwas geladen wird:
   - **Ollama** (MIT), die Laufzeit für die Sprach-KI. Hast du schon ein passendes Ollama, nimmt
     das Spiel dieses (und startet es nur mit deiner Zustimmung).
   - **Das Sprachmodell** `qwen3.5:9b` (Apache-2.0, etwa 6,6 GB).

   Geladen wird nur aus den offiziellen Quellen, in einer festen, getesteten Fassung mit
   Prüfsumme. Du kannst jederzeit abbrechen; beim nächsten Start geht es dort weiter.
4. **Bild-KI (Stufen standard und comfort):**
   1. Installiere ComfyUI selbst (es gehört nicht zum Spiel und steht unter GPL-3.0).
   2. Installiere in ComfyUI die Erweiterung **ComfyUI-GGUF**: im ComfyUI-Manager unter
      „Custom Nodes Manager“ nach „ComfyUI-GGUF“ suchen, installieren, ComfyUI neu starten.
   3. Starte ComfyUI und lass es im Hintergrund laufen.
   4. Nenne im Einrichtungsfenster des Spiels den Ordner deiner ComfyUI-Installation. Nach deiner
      Zustimmung lädt das Spiel die Bildmodelle (feste Fassungen mit Prüfsumme) in deren
      Modellordner. Dateien mit gleichem Namen werden nie ohne Nachfrage überschrieben.
5. Beim Start prüft das Spiel jede KI und zeigt ruhig an, ob alles bereit ist oder was fehlt.

## Wenn etwas nicht geht

Der Selbstcheck beim Start nennt die Ursache. Hier steht, was du tun kannst.

### Sprach-KI

| Meldung | Was du tun kannst |
|---|---|
| Dein Rechner ist für die Sprach-KI zu schwach. | Eine Grafikkarte mit mindestens 8 GB Grafikspeicher. Bis dahin leben die Bewohner nach einfachen Regeln. |
| Ollama fehlt noch. | Im Einrichtungsfenster Ollama herunterladen. |
| Dein Ollama ist zu alt. | Ollama aktualisieren, oder es beenden: Dann nimmt das Spiel sein eigenes. |
| Ollama ist installiert, läuft aber nicht. | Im Einrichtungsfenster erlauben, dass das Spiel Ollama startet. |
| Ollama konnte nicht eingerichtet werden. | Den Download noch einmal starten. Hilft das nicht: Virenscanner prüfen (er darf den Ordner des Spiels nicht sperren) und einen Diagnosebericht erstellen. |
| Das Sprachmodell fehlt noch. | Im Einrichtungsfenster herunterladen. |
| In deinem Ollama liegt eine andere Fassung. | Erlauben, dass die getestete Fassung geladen wird. Deine eigene Fassung unter anderem Namen bleibt unberührt. |
| Das Sprachmodell konnte nicht geladen werden. | Den Download noch einmal starten, er macht dort weiter. Genug freier Platz? |
| Das Sprachmodell ließ sich nicht starten. | Andere Programme schließen, die viel Grafikspeicher brauchen (Spiele, Videobearbeitung, ein zweites ComfyUI-Bild). |

### Bild-KI

| Meldung | Was du tun kannst |
|---|---|
| Die Bild-KI braucht deine eigene ComfyUI-Installation. | ComfyUI installieren (Schritt 4) und im Einrichtungsfenster ihren Ordner nennen. |
| ComfyUI antwortet nicht. | ComfyUI starten und laufen lassen. Es muss auf diesem Rechner laufen (Standard: `http://127.0.0.1:8188`). |
| In deinem ComfyUI fehlt eine Erweiterung. | Die genannte Erweiterung (meist ComfyUI-GGUF) über den ComfyUI-Manager installieren und ComfyUI neu starten. |
| Die Bildmodelle fehlen noch. | Im Einrichtungsfenster herunterladen. |
| Die Bildmodelle konnten nicht geladen werden. | Noch einmal starten, es geht dort weiter. Genug Platz im ComfyUI-Ordner? |

Solange die Bild-KI fehlt, zeigt die Welt einfache Bilder. Das Spiel stürzt nicht ab und wartet
nicht.

### Diagnosebericht

Hilft nichts davon, erstelle im Spiel einen **Diagnosebericht** (Knopf im Einrichtungsfenster).
Er enthält Versionen, den Selbstcheck, die Hardware-Stufe, den Zustand der Modelle und die
letzten Fehler. Er enthält **keine** Zugangsdaten und **keine** Pfade mit deinem Benutzernamen.
Er liegt in `%APPDATA%\Godot\app_userdata\GlassWorld\ai-service\logs\` und bleibt auf deinem
Rechner, bis du ihn selbst weitergibst, zum Beispiel in einem GitHub-Issue.
