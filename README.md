# RSTT

**RSTT — Real-Time Speech-to-Text** is a local-first Windows desktop captioning and text-injection utility by Helios.

RSTT captures the selected Windows playback device with WASAPI loopback, recognizes speech locally with sherpa-onnx, displays a non-activating caption overlay, and pastes pending recognized text into the focused application only on demand. Recognition does not require an account, API key, cloud speech service, analytics endpoint, or transcript upload.

## Current capabilities

- Windows 10/11 x64 WPF application on .NET 8
- Input-driven, bounded WASAPI → resample → ASR pipeline
- Cache-aware Nemotron Streaming English as the recommended realtime model
- Available Nemotron 3.5 multilingual and Parakeet Unified alternatives
- Actionable Qwen3-ASR 0.6B INT8 and Whisper Large v3 Turbo Preview profiles
- Isolated versioned sherpa and whisper.cpp CPU workers
- Optional app-local CUDA 12 Accelerator Pack for sherpa and Whisper
- Resumable, verified in-app downloads with live progress, rate, and ETA
- Automatic compute probing with honest CPU fallback
- Movable, resizable, persistent, no-activate Caption V2 overlay
- Record-and-confirm keyboard/middle-mouse shortcuts with conflict reporting
- Manual clipboard paste of pending batches; no automatic character input
- Local performance telemetry, stall diagnostics, and one controlled stream recovery

## Set up RSTT: a complete guide for first-time users

**Start here if you want to use the app. You do not need to write code or use a terminal.**
RSTT is a portable Windows app: extract its folder, install the Microsoft prerequisite below, and double-click the app.

The release includes the .NET runtime and the app's libraries. **You do not need Visual Studio, the .NET SDK, Python, Git, an account, or an API key to use the release.**
The full package also contains the CUDA and cuDNN libraries used for optional NVIDIA acceleration.

### Step 1. Check your computer and prepare some space

1. Open the Windows **Start** menu, select **Settings**, then **System → About**.
2. Under **Windows specifications**, check that you have Windows 10 or Windows 11.
3. Under **Device specifications**, find **System type**. This release is for **64-bit operating system, x64-based processor**. There is no native ARM64, 32-bit Windows, macOS, or Linux release.
4. Connect the speakers or headphones you normally use. Play a short video and confirm you can hear it.
5. Open **File Explorer → This PC** to check free space. Keep several GB free for the app, and additional space on your Windows user-data drive, usually **C:**, for models. The full GPU-capable app is several GB; each optional Nemotron/Parakeet FP32 GPU model adds about **2.5 GB**. Downloading and extracting a ZIP temporarily needs space for both copies. Model cards show the download size before installation.
6. Connect to the internet for the prerequisite, app, and model downloads. Once your selected model and compute backend are ready, transcription works locally without an internet connection.

**Whisper model requirement:** the bundled Whisper.net 1.9.1 runtime lists **Windows 11** and CPU support for **AVX, AVX2, FMA, and F16C** as prerequisites. On Windows 10, use the recommended Nemotron/sherpa model route. If you have an older processor and Whisper cannot start, use a sherpa model. These additional requirements come from [Whisper.net's versioned documentation](https://github.com/sandrohanea/whisper.net/tree/1.9.1#pre-requisites).

**What you need to install**

| Item | Who needs it? | Where to get it |
| --- | --- | --- |
| RSTT release folder | Everyone | The ZIP supplied by the maintainer, or the app's [GitHub Releases page](https://github.com/helios19980520-max/RSTT/releases) when a release is published |
| Microsoft Visual C++ **x64** Redistributable | Everyone; it may already be installed | [Microsoft's official download page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) or the [official x64 installer](https://aka.ms/vc14/vc_redist.x64.exe) |
| A speech model | Everyone | Download it from **Models** inside RSTT, as described in Step 5 |
| NVIDIA graphics driver | Only for NVIDIA GPU acceleration | [NVIDIA's official driver page](https://www.nvidia.com/en-us/drivers/); see Step 9 |

### Step 2. Download and extract the app

1. Get **`RSTT-win-x64.zip`** from the maintainer. This is the full package with CPU and NVIDIA CUDA workers. A separately provided **`RSTT-win-x64-cpu.zip`** is a smaller CPU-only option.
2. If you are using GitHub, open [Releases](https://github.com/helios19980520-max/RSTT/releases), open the desired release, expand **Assets**, and choose the RSTT ZIP. **Source code (zip)** is the developer project and is not the ready-to-run app. If there is no release asset yet, get the prepared ZIP directly from the maintainer.
3. Open **File Explorer → Downloads** and locate the ZIP. Windows may hide the `.zip` extension; its Type will identify it as a compressed folder.
4. Right-click the ZIP and choose **Extract All…**. Choose an ordinary folder you can write to, such as a folder named **RSTT** inside **Documents**, then click **Extract**. Wait for extraction to finish.
5. Open the extracted folder, then open **RSTT-win-x64** (or **RSTT-win-x64-cpu**). Find **`RSTT.App.exe`**. With extensions hidden, it may display as **RSTT.App**, with Type **Application**.
6. Keep the entire folder together. It contains the executable, `.dll` libraries, `workers`, and other supporting files. Moving only the `.exe` to another folder will break the app.
7. For these instructions in your web browser, double-click **`README.html`** in the same folder. This local copy also works offline.

### Step 3. Install Microsoft's Visual C++ prerequisite

Some of the speech-recognition libraries need the Microsoft Visual C++ runtime even though .NET is already included.

1. Click the [official Microsoft x64 installer download](https://aka.ms/vc14/vc_redist.x64.exe). If the link does not download a file, open [Microsoft's download page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist), find the latest supported **v14** Redistributable table, and use the **X64** link.
2. In your browser's downloads or your **Downloads** folder, double-click **`vc_redist.x64.exe`**.
3. Read the license terms. If you accept them, select the agreement box and click **Install**.
4. If Windows asks whether to allow the Microsoft installer to make changes, verify the publisher is **Microsoft Corporation**, then approve it. On a managed work computer, your IT administrator may need to do this step.
5. Wait for **Setup successful**, then click **Close**. Restart Windows if the installer asks you to.
6. If the installer offers **Repair** instead of **Install**, the runtime is already present. You can close it; use **Repair** later if Windows reports missing Visual C++ runtime files. If it says a newer version is already installed, keep that version.

Choose **x64** for this app, even if an x86 runtime is already installed. Keep any other Visual C++ packages that other apps use. Download this prerequisite from Microsoft; individual DLL download sites are unnecessary.

### Step 4. Open RSTT for the first time

1. Return to the extracted RSTT folder and double-click **`RSTT.App.exe`**.
2. This locally packaged build is unsigned. If Windows displays **Windows protected your PC**, check that you obtained the ZIP from the maintainer. For a copy you trust, **More info → Run anyway** may be available. If company policy blocks it, ask your IT administrator to approve the app.
3. The **Dashboard** should open. A model-not-installed message is normal on a new computer.
4. Open **Settings**. In **Recognition compute**, choose **CPU** for a simple first setup. Once the CPU setup works, Step 9 explains **Auto** and **CUDA**.
5. Keep RSTT running as a normal app. The Microsoft prerequisite may need administrator approval, but everyday transcription does not.
6. Optional: right-click **RSTT.App.exe → Show more options → Send to → Desktop (create shortcut)** on Windows 11. On Windows 10, **Send to** may be directly in the right-click menu. Use a shortcut to keep the executable with its libraries.

### Step 5. Download your first speech model

A **model** is the set of files that lets the app recognize speech. Download it once for each Windows user who will use RSTT.

1. Click **Models** in the left sidebar.
2. Select **Nemotron Streaming English 0.6B INT8** for English speech. It is the recommended realtime starting point. For other languages, inspect the **LANGUAGES** details on a multilingual model before downloading; an English-only model will not recognize every language.
3. Check the model's size and license information. **Preview** models can be used but have incomplete validation; **Coming later** models cannot be installed yet.
4. Click **Download & verify**. Leave the app open and watch the progress, download speed, and remaining time. A large model can take a while on a slow connection.
5. Wait for verification to finish. The app checks the downloaded files before marking the model ready. Downloaded models are stored in your Windows user-data folder, not beside the `.exe`.
6. The downloaded model is selected automatically. If an already-installed model is not active, select it and click **Use now**. Click **Set default** if you want it selected on future launches.
7. If you need to interrupt a download, use **Pause download**. Start it again later to resume. If a download fails, check your connection and disk space, then use **Retry** or **Download & verify**, whichever is available.

You can use **Open model folder** to see the downloaded files. Keep that folder intact; the app manages its contents.

### Step 6. Select and test the audio

**RSTT listens to sound playing through your speakers or headphones. It does not capture your microphone directly.**

1. Play a video, podcast, or other recording with clear speech. Confirm you can hear it.
2. In RSTT, click **Audio**.
3. Under **Output device**, choose the same speakers or headphones that are playing the recording. If you just connected a device, click **Refresh** first.
4. Click **Test audio** while the recording is playing. The test lasts about **five seconds**. Watch **Live input level**; the meter should move when sound is present.
5. If the meter stays still, open Windows **Settings → System → Sound** and check which output device is selected. Select the matching device in RSTT and test again. Also check that the player is playing and not muted.
6. If you change between speakers, USB headphones, and Bluetooth headphones later, repeat this step. Apps with their own audio-output setting must also use the device selected in RSTT.

### Step 7. Start transcription and show captions

1. Return to **Dashboard** and click **Start listening**.
2. Wait for the model to load, then keep playing the speech recording. The first load may take longer than later starts.
3. Confirm that words appear in **Live transcript**. Live words may change as the model hears more speech.
4. For floating captions, open **Settings** and turn on **Show live caption overlay**, or press **Ctrl+Alt+C**. The **Captions** page contains appearance and positioning controls.
5. Click **Stop listening**, or press **Ctrl+Alt+R**, when you want to stop. The same shortcut starts listening again.

The overlay does not take keyboard focus away from your other app. RSTT transcribes speech; it does not translate it into a different language.

### Step 8. Paste recognized text into another app

1. Let a few words appear in **Live transcript**.
2. Open an empty document in **Notepad** for a first test. Click its writing area so the text cursor is visible.
3. Press the **middle mouse button**: press down on the scroll wheel, rather than scrolling it. The default shortcut is shown as **MMB**.
4. The pending transcript is pasted into the document and consumed from RSTT's pending batch. New speech waits for your next paste. The clipboard retains the text you pasted.
5. To use a keyboard shortcut instead, open **Settings → Global shortcuts → Paste Recognized Sentences**, click **Record Shortcut**, press your chosen combination, then click **Confirm**. Resolve any conflict message by choosing another combination.
6. If a paste fails, the pending text remains available. Click a normal text field and try again. Windows can block paste into an app running as Administrator when RSTT is running normally.

### Step 9. Optional: use an NVIDIA GPU

You can continue using **CPU** on a computer with NVIDIA, AMD, Intel, or no dedicated graphics card. CUDA acceleration requires a compatible NVIDIA GPU and its driver. The full **RSTT-win-x64** release already includes the app's CUDA 12 and cuDNN libraries; using it does not require installing the CUDA Toolkit, cuDNN, Python, or a separate Accelerator Pack.

1. To check your graphics card, right-click the Windows **Start** button, select **Device Manager**, then expand **Display adapters**. Look for an **NVIDIA** device.
2. If needed, open [NVIDIA's official driver page](https://www.nvidia.com/en-us/drivers/). Select your graphics-card model and Windows version, download the matching driver, run its installer, follow its on-screen instructions, and restart if requested. On a managed PC, ask IT to handle the driver installation.
3. Open RSTT and stop listening before changing the compute setting.
4. Open **Settings → Recognition compute** and select **Auto**. Auto tries the packaged CUDA worker and falls back to CPU if the GPU cannot run the selected model. **CUDA** also reports failed initialization before falling back.
5. Keep internet access available for the first GPU setup. Nemotron English and Parakeet use different GPU weights, which the app downloads when needed; allow about **2.5 GB** extra per model.
6. Under **Settings → Diagnostics**, click **Run self-test**. This uses the app's bundled speech sample to check the chosen backend. Wait for the checks to finish, and read any failed check or fallback explanation.
7. Return to **Dashboard**, start listening, and play speech. **CUDA Active** appears only after real audio has been decoded on the GPU. A detected NVIDIA card by itself does not prove GPU recognition is running.
8. If the GPU setup fails, select **CPU** and continue using RSTT. The troubleshooting table below explains where to find diagnostics.

If you received the **CPU-only ZIP**, get the full ZIP to use CUDA and extract it into a fresh folder. Your downloaded models and settings remain in Windows user data. The **Install Accelerator Pack** button opens the releases page; it does not install a graphics driver or download a pack automatically.

### Step 10. Close, update, or remove the app

- **Close:** if closing the window leaves RSTT running, look in the Windows notification area near the clock (use the **^** arrow for hidden icons). Right-click the RSTT icon and choose **Exit**. Double-clicking the icon reopens the window.
- **Update:** exit RSTT, extract the new ZIP into a fresh folder, and open the new **RSTT.App.exe**. Update your desktop shortcut to point to it. Settings and models remain in `%LOCALAPPDATA%\Helios\RSTT`, so you normally do not need to download models again. Keep the old app folder until the new version works.
- **Remove the app:** exit it, then delete the extracted app folder and its shortcut.
- **Remove downloaded models/settings too:** press **Windows+R**, paste `%LOCALAPPDATA%\Helios\RSTT`, and press **Enter**. Delete this folder only if you want to erase all RSTT settings, downloaded models, and logs for your Windows account. Removing only the app folder leaves this data in place.

### First-time setup troubleshooting

| What you see | What to do |
| --- | --- |
| `RSTT.App.exe` is missing | Extract the app ZIP completely. Check that you downloaded the release asset, rather than GitHub's source-code ZIP. Windows may hide the `.exe` extension. |
| Windows mentions `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, or `MSVCP140.dll` | Install or repair the official Microsoft **x64** Visual C++ Redistributable in Step 3, then reopen RSTT. |
| A worker or another DLL is missing, or Windows asks for .NET | Re-extract the complete release ZIP into a fresh folder. Run the top-level `RSTT.App.exe`; keep its `.dll` files and the entire `workers` folder. Build-output folders and individual EXEs are not the portable release. |
| Model download fails or pauses | Check internet access and free space on your Windows user-data drive. Resume/retry in **Models**. Network filtering can block model hosts; on a managed network ask IT to check access. |
| Start listening is unavailable | Finish installing a usable model, select it with **Use now** if needed, and select an audio-output device. |
| No audio level or no words | Repeat **Test audio** while speech is playing through the selected output device. Speaking into a microphone alone does not feed this app. Check the selected model supports the spoken language. |
| Text appears slowly or falls behind | Try the recommended Nemotron English model for English, close other heavy apps, and try a faster recognition mode. If available, verify NVIDIA acceleration with Step 9. |
| CUDA is unavailable or RSTT uses CPU | Check that you have the full release, a compatible NVIDIA GPU/driver, enough GPU memory, and a CUDA-capable model. Use **Run self-test** and read the fallback reason. CPU remains usable. |
| The shortcut does nothing | Check **Settings → Global shortcuts** for conflicts. MMB means pressing the wheel. For paste, focus a normal editable text field with pending text available. |
| The window disappeared | Open hidden notification-area icons near the clock and double-click RSTT. It may have minimized to the tray. |
| You need to report a problem | Open **Settings → Diagnostics → Copy diagnostics**, then paste the report into your message to the maintainer. **About → Open local logs** opens the log folder. Describe what you clicked and what appeared. |

**Setup is complete when:** audio moves the meter, the selected model is active, speech appears in Live transcript, and your paste shortcut inserts it into a test document.

## Models

| Model | Integration | Language | Streaming | Profile |
| --- | --- | --- | --- | --- |
| Nemotron Streaming English 0.6B INT8 | Available, recommended | English | Native/cache-aware | Balanced, 560 ms |
| Nemotron 3.5 Streaming Multilingual 0.6B INT8 | Available | 19 transcription-ready locales | Native/cache-aware | Balanced, 560 ms |
| Parakeet Unified English 0.6B INT8 | Available | English | Buffered | Accurate, 1120 ms |
| Qwen3-ASR 0.6B INT8 | Preview, actionable | 30 languages + 22 Chinese dialects | Segmented realtime | Silero VAD 200/500 ms |
| Whisper Large v3 Turbo Q5_0 | Preview, actionable | Multilingual auto/manual | Segmented realtime | Default Whisper profile |
| Whisper Large v3 Turbo full | Preview, actionable | Multilingual auto/manual | Segmented realtime | Maximum quality |

Qwen and both Whisper profiles have pinned download validation and real CPU/RTX
2060 CUDA decode evidence. They remain Preview until the full multilingual,
accented, quiet, and no-trailing-silence corpus gate passes. Coming-later
entries have no download or activation command. See [Model
management](docs/MODELS.md) and the [verified model
matrix](docs/MODEL_MATRIX.md).

## Compute backends

The current app uses sherpa-onnx 1.13.8 and worker protocol 2 (Accelerator Pack
1.0.2). CUDA selection verifies native node placement and a real warmup decode.
Parakeet and Nemotron English use verified FP32 weights on CUDA and INT8 weights
on CPU. Missing GPU weights are downloaded on the first CUDA selection.

Build and publish include CUDA workers when the matching local Accelerator
Pack has been built. CPU workers remain isolated and work without CUDA.

The optional Accelerator Pack installs versioned app-local sherpa CUDA
12.8/cuDNN 9.24 and Whisper CUDA 12 workers. Auto verifies handshake, provider,
model load, and warmup before selection, terminates a failed CUDA process before
CPU fallback, and displays `CUDA Active` only after real session inference.
RTX 2060 CPU/CUDA measurements are recorded in the documentation.

See [Compute backends](docs/COMPUTE_BACKENDS.md) for packaging and fallback details.

## Captions and paste on demand

Live Transcript shows the pending batch. Press **MMB** to paste it into a focused
text input using the clipboard. Pasted text is consumed; speech arriving during
paste remains for the next batch. Failed pastes retain pending text. RSTT never
automatically types individual characters. The clipboard retains the pasted text.

The caption overlay remains bounded and non-activating.

See [Paste and recognition update](docs/PASTE_AND_RECOGNITION_UPDATE.md) for behavior,
measured GPU results, remaining model limits, and validation details.

## Global shortcuts

| Default | Action |
| --- | --- |
| `Ctrl+Alt+R` | Start/stop listening |
| `MMB` | Paste Recognized Sentences |
| `Ctrl+Alt+C` | Show/hide captions |

Click **Record Shortcut**, press a single key, keyboard combination, or MMB (including MMB+B),
and click **Confirm**. Cancel, Reset, and Clear are available. Clear disables the
binding. Keyboard conflicts and duplicate RSTT bindings preserve the old shortcut.

## Runtime architecture

```text
WASAPI callback
  → bounded raw-packet channel
  → one downmix/resample/meter worker
  → bounded normalized-audio channel
  → one descriptor-selected isolated sherpa or whisper.cpp worker
  → bounded ordered result channel
  ├─ coalesced WPF/caption publication (20 Hz maximum for partials)
  └─ pending batch buffer → explicit shortcut → clipboard paste
```

The WASAPI callback copies into pooled memory and returns; it does not resample, decode, log per packet, invoke WPF, or wait on downstream work. Queue depth and age are bounded. Stop/start creates a new generation and stale results are ignored.

See [Architecture](docs/ARCHITECTURE.md) and [Performance](docs/PERFORMANCE.md).

## Performance troubleshooting

The recommended model should maintain realtime factor (RTF) below `1.0` on supported hardware. If RTF is above `1.0`:

- use Nemotron Streaming English rather than buffered Parakeet;
- choose the Fast/Balanced profile supported by the installed model;
- leave the CPU thread limit on Auto unless profiling supports a change;
- use a verified GPU package when one is actually available;
- close competing CPU-heavy inference or media-processing workloads.

Do not raise the whole process to Windows Realtime priority. Correct callback and queue design protects playback without risking system responsiveness.

Operational diagnostics are in `%LOCALAPPDATA%\Helios\RSTT\Logs`. Logs include lifecycle, backend, model, queue, decode, and sanitized error data—not raw audio or complete transcript text.

## Local data

| Data | Location |
| --- | --- |
| Settings | `%LOCALAPPDATA%\Helios\RSTT\settings.json` |
| Models | `%LOCALAPPDATA%\Helios\RSTT\Models` |
| Resumable staging | `%LOCALAPPDATA%\Helios\RSTT\Models\.downloads` |
| Logs | `%LOCALAPPDATA%\Helios\RSTT\Logs` |

Model artifacts are not bundled in the repository or publish output.

## Build, test, and publish from source

This section is for developers and maintainers creating a release. Ordinary users can follow the setup guide above.

### Install the development tools

1. Use a Windows x64 computer and install the Visual C++ prerequisite from the user guide.
2. Open [Microsoft's .NET 8 download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Under **SDK**, choose the Windows **x64 installer**, run it, and complete the installation. The SDK includes the build tools; downloading only the runtime is not enough for development.
3. Install **PowerShell 7** using [Microsoft's Windows installation guide](https://learn.microsoft.com/en-us/powershell/scripting/install/installing-powershell-on-windows). The x64 MSI installer linked there is one option. The release scripts require PowerShell 7; the built-in **Windows PowerShell 5.1** does not provide all their commands.
4. Get the source from [the RSTT repository](https://github.com/helios19980520-max/RSTT). **Code → Download ZIP**, followed by **Extract All…**, works without installing Git. If Git is already installed, cloning the repository works too.
5. Open **PowerShell 7** from the Start menu. Enter `Set-Location` followed by the extracted repository folder in quotes, for example:

   ```powershell
   Set-Location "C:\Users\YourName\Documents\RSTT-main"
   ```

   Replace that example with your actual folder. It must contain **RSTT.sln**, **README.md**, **src**, and **scripts**.

6. Confirm the SDK is available, then restore the project's dependencies. Restore downloads the NuGet packages listed in the project; it needs internet access on a fresh machine.

   ```powershell
   dotnet --list-sdks
   dotnet restore RSTT.sln
   ```

   If `dotnet` is not recognized, close and reopen PowerShell after installing the SDK. Confirm an **8.0** SDK, or a compatible newer SDK, appears in the list.

### Run and test the source

```powershell
dotnet build RSTT.sln -c Release --no-restore
dotnet test RSTT.sln -c Release --no-restore
dotnet run --project src\RSTT.App\RSTT.App.csproj -c Release --no-build
```

Install models through the app as described in the user guide. The **CUDA runtime pack absent** build message means this machine has not built the optional pack; the CPU app remains available.

### Create a CPU-only release

The supplied application artwork is stored in `src\RSTT.App\Assets\RSTT.png`; its multi-size Windows icon is `RSTT.ico` in the same folder. Both are embedded in the app. To replace the artwork for a future build, run the following before publishing, using your image's actual path:

```powershell
pwsh -NoProfile -File .\scripts\Convert-AppIcon.ps1 -ImagePath "C:\path\to\icon.png"
```

In PowerShell 7, from the repository folder:

```powershell
pwsh -NoProfile -File .\scripts\Publish-Release.ps1 -CpuOnly
```

The script restores and publishes the application and workers, includes .NET and the native CPU libraries, copies the documentation and available dependency notices, creates local HTML guides, checks required files, and writes a file manifest, ZIP, and SHA-256 checksum.

```text
artifacts\release\RSTT-win-x64-cpu\RSTT.App.exe
artifacts\release\RSTT-win-x64-cpu\README.html
artifacts\release\RSTT-win-x64-cpu.zip
artifacts\release\RSTT-win-x64-cpu.zip.sha256
```

### Create the full release with NVIDIA acceleration

The **build computer** needs the pinned CUDA runtime pack. **End-user computers** receive those libraries inside the full release and only need an NVIDIA driver for acceleration.

1. Prepare **CUDA Toolkit 12.8** and the **cuDNN 9.24.0.43** runtime and its license on the build computer. The pack script defaults to `C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8` and searches Python-installed cuDNN; other locations need `-CudaToolkitRoot` and `-CudnnBinPath`. The cuDNN license must also be discoverable in the expected package layout. See [Compute backends](docs/COMPUTE_BACKENDS.md) and the script for the exact build prerequisites. Official downloads: [CUDA Toolkit archive](https://developer.nvidia.com/cuda-toolkit-archive) and [cuDNN](https://developer.nvidia.com/cudnn-downloads).
2. Build the pack. This downloads and verifies the pinned sherpa CUDA archive when it is not already cached:

   ```powershell
   pwsh -NoProfile -File .\scripts\Build-AcceleratorPack.ps1 -Configuration Release
   ```

3. Publish the full app. If the matching pack already exists, you can start with this command:

   ```powershell
   pwsh -NoProfile -File .\scripts\Publish-Release.ps1
   ```

4. Find the completed release files:

   ```text
   artifacts\release\RSTT-win-x64\RSTT.App.exe
   artifacts\release\RSTT-win-x64\README.html
   artifacts\release\RSTT-win-x64\release-manifest.json
   artifacts\release\RSTT-win-x64.zip
   artifacts\release\RSTT-win-x64.zip.sha256
   ```

The full folder includes both CPU workers, both CUDA workers, their required native libraries, .NET, the diagnostic audio sample, documentation, and notices. Models are downloaded by the user and are not included in the ZIP. The Microsoft Visual C++ Redistributable and NVIDIA graphics driver are system prerequisites described in the user guide.

The script preserves existing releases by refusing to overwrite them. For another build, choose a new folder inside **artifacts**, for example:

```powershell
pwsh -NoProfile -File .\scripts\Publish-Release.ps1 -OutputRoot .\artifacts\release-next
```

Use `-NoArchive` if you only need the application folder. Keep the entire folder together when copying it to another computer. The script creates local artifacts; no public release is created automatically.

**Choose a distribution channel:** [GitHub limits each release asset to under 2 GiB](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#storage-and-bandwidth-quotas). The full CUDA ZIP from this build is larger than that limit. Share that complete ZIP and its checksum through a file host that supports its size, or copy the complete extracted folder to the recipient. The smaller CPU-only ZIP can be uploaded directly to GitHub Releases. The `.sha256` file contains the ZIP's integrity checksum; keep it with the corresponding archive.

For a raw development publish without the release guide/archive step, the existing Visual Studio profile remains available:

```powershell
dotnet publish src\RSTT.App\RSTT.App.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=win-x64
```

Its output is **artifacts\publish\win-x64**. Add `-p:IncludeAcceleratorWorkers=false` to omit the CUDA workers even when a pack exists locally.

The Windows text-injection integration fixture is non-destructive: it always
uses a uniquely named temporary document and a fresh Notepad HWND. It never
types into or closes a user document.

## Privacy and licensing

Captured audio is transient and is never written to disk. Pending text stays in memory until pasted or cleared and is not saved automatically. Internet access is limited to installing models, including GPU weights on the first CUDA selection.

RSTT source is licensed under the [MIT License](LICENSE). Dependencies and separately downloaded models retain their own terms; see [Third-party notices](THIRD_PARTY_NOTICES.md).
