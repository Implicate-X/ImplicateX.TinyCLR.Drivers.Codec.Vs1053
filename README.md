📘 README.md

# VS1053B TinyCLR Driver  
A robust, fully re-engineered TinyCLR driver for the **VS1053B audio codec**, optimized for MP3/WAV streaming, strict SCI/SDI domain separation, deterministic DREQ synchronization, and stable operation across FEZ Duino, SITCore boards, and Adafruit Music Maker.

This driver corrects structural issues found in several implementations in C++ and provides a technically accurate, empirically validated architecture for reliable audio playback under TinyCLR OS.

---

## ✨ Features

- **Separate SPI domains**
  - SCI (control-plane) @ **250 kHz**
  - SDI (stream-plane) @ **4 MHz**
  - Matches the VS1053B’s internal timing expectations.

- **Manual chip-select via GPIO**
  - Ensures stable transactions on both SCI and SDI channels.

- **Strict DREQ synchronization**
  - Deterministic data flow, no decoder underruns.

- **Robust hardware reset sequence**
  - Stable boot path compatible with Adafruit Music Maker and generic VS1053 boards.

- **MP3 and WAV streaming**
  - ID3v2 skipping  
  - 512-byte chunk streaming  
  - Tail-byte handling  
  - VS10xx-compliant filler flush

- **Sine test mode**
  - Quick hardware validation.

- **Realtime MIDI bootstrap (SCI-based)**
  - Optional UART-MIDI path for Music Maker boards.

- **Thread-safe SPI access**

---

## 📦 Installation

Add the class to your TinyCLR project or include it as a standalone NuGet package.

---

## 🧩 Hardware Setup

### Required pins
- **CMD_CS** – SCI chip-select  
- **DAT_CS** – SDI chip-select  
- **DREQ** – Data Request (must be wired correctly)  
- **RESET** – Hardware reset  
- **SPI** – Shared bus for SCI and SDI

### Supported boards
- e.g. FEZ Duino  
- SITCore SC13048/ SC20100 / SC20260  
- Adafruit Music Maker (SPI path only, SDI-MIDI disabled)

---

## 🚀 Quickstart

### Initialize

```csharp
var codec = new Vs1053(
    spiControllerName: "SPI1",
    cmdCsPinID: 5,
    datCsPinID: 6,
    dreqPinID: 7,
    resetPinID: 8);

codec.Initialize();
codec.SetVolume(200, 200);

Play MP3
codec.PlayMp3(@"D:\music\track01.mp3");

Play WAV
codec.PlayWav(@"D:\sounds\effect.wav");

Sine Test
codec.RunStartupSineTest();

⚙️ Architecture Overview

SCI (Control-plane)
Register access
Clock configuration
Status/health checks
Soft reset
Sine test mode
Realtime MIDI bootstrap
→ 250 kHz for maximum stability.
SDI (Stream-plane)
MP3 bitstream
WAV data
Filler bytes
Streaming chunks
→ 4 MHz for stable playback of large MP3 files.

DREQ
The VS1053B’s only true synchronization anchor.The driver strictly waits for DREQ-high before every SDI operation.

🛡️ Improvements over GHI Driver

This driver resolves the following issues in the original GHI implementation:
Issue
Fix
SCI & SDI share same SPI clock
independent frequencies (250 kHz / 4 MHz)
SPI device recreated per call
static instances
DREQ wait without timeout
bounded timeout + exception
Tail bytes dropped
full tail handling
No synchronization
global SPI lock
Weak init verification
full register health checks
Unstable streaming path
deterministic chunk pipeline

📁 Supported Formats

MP3
ID3v2 skip
512-byte streaming
2052 filler flush
DECODE_TIME check
WAV
Direct streaming without header manipulation

🎵 MIDI

Realtime MIDI over SCI is supported (as used internally by Adafruit Music Maker).
MIDI over SDI is intentionally disabled, because Music Maker boards pull GPIO0 permanently low.

🧪 Diagnostics

RunStartupSineTest()
CommandRead() / CommandWrite()
SoftReset()
EnableAnalogPath()
WaitDreqStableHigh()

📜 License

See license.txt — fully permissive, free for all use.

🤝 Contributing

Pull requests welcome.This driver is intended as a reference implementation for stable, reproducible audio pipelines under TinyCLR OS.