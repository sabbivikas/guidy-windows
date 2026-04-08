# Clicky for Windows

An AI buddy that lives next to your cursor. It can see your screen, talk to you, and point at stuff. Kinda like having a real teacher next to you.

Hold **Ctrl+Alt** to talk. Release to send. It'll respond with voice and fly the cursor to whatever it's talking about.

Windows port of [Clicky](https://github.com/farzaa/clicky) by [@farzaa](https://github.com/farzaa), originally built for macOS.

## Get started with Claude Code

The fastest way is with [Claude Code](https://docs.anthropic.com/en/docs/claude-code).

Once you have it running, paste this:

```
Hi Claude.

Clone https://github.com/shreshth-s/clicky-windows.git into my current directory.

I want to get Clicky running locally on Windows.

Help me set up everything — the Cloudflare Worker with my own API keys, the proxy URLs, and getting it building with dotnet. Walk me through it.
```

That's it. It'll clone the repo, read the docs, and walk you through the whole setup.

## Manual setup

### Prerequisites

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Node.js 18+ (for the Cloudflare Worker)
- A [Cloudflare](https://cloudflare.com) account (free tier works)
- API keys for: [Anthropic](https://console.anthropic.com), [AssemblyAI](https://www.assemblyai.com), [ElevenLabs](https://elevenlabs.io) (Starter plan or higher)

### 1. Set up the Cloudflare Worker

The Worker is a tiny proxy that holds your API keys. The app talks to the Worker, the Worker talks to the APIs. Your keys never ship in the binary.

```bash
cd worker
npm install
npx wrangler secret put ANTHROPIC_API_KEY
npx wrangler secret put ASSEMBLYAI_API_KEY
npx wrangler secret put ELEVENLABS_API_KEY
npx wrangler deploy
```

Set your ElevenLabs voice ID in `worker/wrangler.toml`:

```toml
[vars]
ELEVENLABS_VOICE_ID = "your-voice-id-here"
```

### 2. Set your Worker URL

Open `clicky-windows/CompanionManager.cs` and update:

```csharp
public const string WorkerBaseUrl = "https://your-worker-name.your-subdomain.workers.dev";
```

### 3. Build and run

```bash
cd clicky-windows
dotnet run
```

The app appears in your system tray. Hold **Ctrl+Alt** to talk.

> **Note:** Run as administrator (or from an elevated terminal) to enable screen capture of elevated windows like Task Manager.

## Architecture

Native Windows app — no Electron. C# + WPF (.NET 8) running as a system tray app with a full-screen transparent click-through overlay.

Push-to-talk streams audio over a WebSocket to AssemblyAI, sends the transcript + a screenshot to Claude via streaming SSE, and plays the response through ElevenLabs TTS. Claude can embed `[POINT:x,y:label:screenN]` tags to fly the cursor to specific UI elements across multiple monitors. All APIs are proxied through a Cloudflare Worker so keys never touch the client.

## Project structure

```
clicky-windows/           # C# WPF app
  CompanionManager.cs       # Central state machine + pipeline
  OverlayWindow.xaml.cs     # Full-screen transparent cursor overlay
  CompanionPanelWindow.xaml # Tray panel UI
  TrayManager.cs            # System tray icon
  Services/
    AssemblyAiTranscriptionService.cs  # Real-time WebSocket transcription
    ClaudeApiClient.cs                 # Claude streaming SSE client
    ElevenLabsTtsClient.cs             # TTS playback
    AudioCaptureService.cs             # Mic capture (NAudio)
    GlobalHotkeyMonitor.cs             # Win32 low-level keyboard hook
    ScreenCaptureUtility.cs            # Multi-monitor screenshot
worker/                   # Cloudflare Worker proxy
  src/index.ts              # Routes: /chat, /tts, /transcribe-token
CLAUDE.md                 # Full architecture doc
```

## Contributing

PRs welcome. If you're using Claude Code, it already knows the codebase — just tell it what you want to build and point it at `CLAUDE.md`.
