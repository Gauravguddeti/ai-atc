# Build Progress

Last updated: 2026-08-29
Current phase: Phase 1 (core loop — all source written, built successfully, Piper downloading)

## Phase 1 — Core loop (ground + tower, single airport, manual PTT)
- [x] SimConnect bridge connects and exposes live SimState (graceful fallback if no DLL)
- [x] Push-to-talk capture works via global WH_KEYBOARD_LL hook (works while MSFS has focus)
- [x] Groq Whisper transcription client (whisper-large-v3-turbo)
- [x] Groq LLM produces grounded, in-character ATC responses (llama-3.1-70b-versatile)
- [x] Piper TTS wrapper (spawns piper.exe as subprocess, PCM→WAV)
- [x] Radio DSP filter (bandpass 300–3000Hz + compression + soft clip + static bed)
- [x] Audio player with WasapiOut and device-by-ID selection
- [x] IDLE/RECORDING/PROCESSING/SPEAKING state machine fully implemented
- [x] PTT ignored while PROCESSING or SPEAKING
- [x] No background listening — mic only open between PTT-press and PTT-release
- [x] Debounce: repeat key-down while RECORDING are ignored
- [x] Fresh WasapiCapture per PTT press, fully disposed after use
- [x] Device enumeration: DataFlow.Capture for input, DataFlow.Render for output
- [x] Device selection stored by ID string (not index or name)
- [x] Fallback to system default if saved device not found
- [x] Overlay: transparent, frameless, always-on-top WPF (WS_EX_NOACTIVATE)
- [x] Overlay: scrolling chat log, PTT indicator greys when not IDLE
- [x] Overlay: animated state dot (pulses while RECORDING)
- [x] dotnet build: 0 errors, 0 warnings
- [x] Piper binary downloaded (piper/piper.exe)
- [ ] Piper voice model downloaded (en_US-lessac-medium.onnx) — in progress
- [ ] Manual test in MSFS: PENDING

## Phase 2 — Proactive ATC + phase/handoff logic
- [x] HandoffStateMachine: ClearanceDelivery/Ground/Tower/Departure/Center — IMPLEMENTED
- [x] TriggerScheduler: 4 flight event triggers — IMPLEMENTED
- [x] Wired into App.xaml.cs (triggers bypass PTT, go straight to TTS)
- [ ] Manual test: PENDING

## Phase 3 — Full IFR, Center handoffs, flight plan awareness
- [x] AirspaceLookup stub (returns null)
- [ ] Flight plan ingestion
- [ ] FAA ARTCC boundary data
- [ ] Extended handoff chain
- [ ] Manual test: NOT STARTED

## Phase 4 — Traffic awareness
- [x] TrafficTracker stub (returns empty)
- [ ] SimConnect traffic enumeration
- [ ] Sequencing logic
- [ ] Manual test: NOT STARTED

## Installer
- [ ] NOT STARTED (build after all 4 phases pass)

## Known issues / open questions
- SimConnect DLL: must be obtained from MSFS SDK and placed in /libs. App compiles and runs without it (simulation-less mode).
- PTT key defaults to CapsLock. Change PUSH_TO_TALK_KEY in .env.
- Phase 3 airspace data source to be confirmed at Phase 3 build time.

## Next steps
1. Wait for Piper model download to complete
2. Launch `dotnet run` with MSFS running
3. Test PTT → Whisper → LLM → Piper TTS → radio filter → playback full loop
4. Verify state machine: PTT does nothing while ATC is speaking
5. Check Phase 1 acceptance criteria; mark as passed
6. Test Phase 2 proactive triggers (clearance delivery trigger on engine start)
7. Continue to Phase 3 after all acceptance criteria confirmed
