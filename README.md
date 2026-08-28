# MSFS AI ATC

An AI-powered Air Traffic Control overlay for Microsoft Flight Simulator 2020/2024.

Hold your PTT key → speak → real ATC voice responds using Groq AI + local TTS.

---

## For My Friend — Setup Guide (No Coding Needed)

### What you need before starting
- A PC running **Windows 10 or 11 (64-bit)**
- **Microsoft Flight Simulator 2020 or 2024** installed
- **Internet connection** (for the one-time download, ~70 MB)
- The **Groq API key** I sent you on WhatsApp

---

### Step 1 — Install .NET 8 (one-time, takes 2 min)

1. Go to this link: **https://dotnet.microsoft.com/en-us/download/dotnet/8.0**
2. Under **".NET Desktop Runtime 8"**, click **"x64"** to download
3. Run the installer — click Next, Next, Finish
4. Done ✅

> You only ever do this once. If you already have it, skip this step.

---

### Step 2 — Get the app from GitHub

1. Go to: **https://github.com/Gauravguddeti/ai-atc**
2. Click the green **"Code"** button → **"Download ZIP"**
3. Extract the ZIP to anywhere, e.g. `C:\AI-ATC\`

---

### Step 3 — Add your API key

1. Open the extracted folder
2. Find the file called **`.env`** (it looks like a settings file)
   - If you can't see it, open File Explorer → View → check **"Hidden items"**
3. Right-click `.env` → **Open with Notepad**
4. You'll see this line:
   ```
   GROQ_API_KEY=gsk_xxxxxxxx...  ← (already filled in by Gaurav)
   ```
5. That key is already there — Gaurav put it in. You don't need to change anything.
6. Close Notepad. Done ✅

---

### Step 4 — Run the setup (one-time)

1. In the extracted folder, find **`SETUP (Run This First).bat`**
2. Double-click it
3. If Windows asks "Do you want to allow this app to make changes?" → click **Yes**
4. A black window appears and starts downloading things — wait for it
5. When it says **"Setup complete!"** → press any key to close it ✅

> This downloads the AI voice (~70 MB) and builds the app. Only needed once.

---

### Step 5 — Launch the app

1. **Start MSFS first** and load into any airport
2. Double-click **`Start AI ATC.bat`**
3. A small transparent overlay appears in the top-left corner of your screen

---

### Step 6 — Using it

| What you want | What to do |
|---|---|
| Talk to ATC | Hold **CapsLock**, speak, release |
| ATC will respond | Voice comes through your speakers/headset |
| Move the overlay | Click and drag the title bar |
| Close the app | Click the **✕** button on the overlay |

**The overlay shows:**
- 🔴 Red dot = not connected to MSFS (make sure the sim is running)
- 🟡 RECORDING = it's listening to you
- 🟠 PROCESSING = thinking...
- 🟢 SPEAKING = ATC is talking (don't press PTT now)

---

### Changing the PTT key (optional)

If you don't want to use **CapsLock**, open `.env` in Notepad and change:
```
PUSH_TO_TALK_KEY=CapsLock
```
To any of these: `RightCtrl`, `RightShift`, `F12`, `Space`, `Tab`

---

### Troubleshooting

| Problem | Fix |
|---|---|
| "⚠ Rate limit hit — wait ~60s" | You used it a lot. Wait 60 seconds, it resets automatically |
| No voice / silence | Make sure your speakers are on. Run setup again if Piper failed |
| Overlay says "SIM: Disconnected" | Launch MSFS first, then start the app |
| App doesn't open | Right-click `Start AI ATC.bat` → Run as administrator |
| Setup failed | Make sure you have internet, then run `SETUP (Run This First).bat` again |

---

## For Developers

```
dotnet build    # build
dotnet run      # run in debug mode

# Verified live Groq models (August 2026):
# LLM:  openai/gpt-oss-120b  (primary)  + qwen/qwen3.8-27b (fallback)
# STT:  whisper-large-v3-turbo
```

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`docs/PROGRESS.md`](docs/PROGRESS.md) for internals.

### Rate limit strategy
- 2 API keys in `.env` (`GROQ_API_KEY` + `GROQ_API_KEY_2`)
- On 429: auto-rotates to next key and next model
- `max_tokens = 100` — ATC phrases are short, keeps token spend tiny (~500 tokens/call)
- History trimmed to last 6 turns — context stays lean
- If all keys exhausted: overlay shows ⚠ warning, auto-resets after 60 s
