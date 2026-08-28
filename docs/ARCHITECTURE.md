# MSFS AI ATC — Architecture Overview

## Component Map

```
┌─────────────────────────────────────────────────────────────────┐
│                        App.xaml.cs (Orchestrator)               │
│  Wires all components, manages lifecycle, drives the pipeline   │
└────┬────────┬────────┬───────────┬────────────┬────────────────┘
     │        │        │           │            │
     ▼        ▼        ▼           ▼            ▼
 SimBridge  Audio   Speech      AtcBrain    Overlay
  (data)    (I/O)   (STT/TTS)   (LLM)       (UI)
```

## Data Flow — Pilot speaks

```
[Pilot holds PTT key]
       │
       ▼
GlobalHotkeyHook.PttKeyDown
       │
       ▼
VoicePipeline: IDLE → RECORDING
  └── opens WasapiCapture (fresh, 16kHz mono)
       │
[Pilot releases PTT key]
       │
       ▼
VoicePipeline: RECORDING → PROCESSING
  └── StopRecording → OnRecordingStopped → fires AudioCaptured(wavBytes)
       │
       ▼
App.OnAudioCaptured:
  1. GroqWhisperClient.TranscribeAsync(wavBytes) → transcript
  2. if (empty) → SetIdle(); return
  3. Overlay.AddPilotMessage(transcript)
  4. AtcBrainService.GetResponseAsync(transcript, simState) → atcResponse
  5. Overlay.AddAtcMessage(atcResponse)
  6. VoicePipeline.SetSpeaking()
  7. PiperTtsWrapper.SynthesizeAsync(text) → rawWavBytes
  8. RadioFilter.Apply(samples) → filteredSamples
  9. AudioPlayer.PlayWithRadioFilterAsync(filtered) — blocks until done
  10. VoicePipeline.SetIdle()
```

## Data Flow — Proactive ATC (Phase 2)

```
TriggerScheduler polls SimState every 2s
  → detects event (e.g. stationary + engines running)
  → fires TriggerFired("...trigger description...")
       │
       ▼
App.OnTriggerFired:
  1. if (VoicePipeline.State != IDLE) return  ← never interrupts
  2. AtcBrainService.GetUnpromptedTransmissionAsync(trigger, simState)
  3. Overlay.AddAtcMessage(response)
  4. SpeakAtcResponse(response)  ← same TTS path as pilot-triggered
```

## SimState as Ground Truth

Every LLM call receives `SimState.ToContextString()` as part of the system prompt.
The LLM's only job is producing realistic phraseology from these facts.
It never invents airports, runways, frequencies, or traffic.

## Half-Duplex State Machine

```
IDLE  →  (PTT pressed)  →  RECORDING  →  (PTT released)  →  PROCESSING  →  SPEAKING  →  IDLE
```

- PTT key-down is IGNORED in any state other than IDLE
- PTT key-down debounce: `_isKeyCurrentlyDown` flag in GlobalHotkeyHook
- This prevents both random input and AI-speech interruption
- Single module (VoicePipeline) is the only component that touches mic start/stop

## Audio Device Enumeration

- Input devices: `MMDeviceEnumerator(DataFlow.Capture)` — microphones only
- Output devices: `MMDeviceEnumerator(DataFlow.Render)` — speakers/headphones only
- Stored by `MMDevice.ID` string — stable across reboots, survives device reconnect
- Fallback: system default + warning message if saved ID not found
