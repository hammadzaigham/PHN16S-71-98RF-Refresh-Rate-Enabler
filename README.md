# PHN16S-71-98RF Refresh Rate Enabler

Adds 60, 75, 100, 120, 144, and 165 Hz on the Acer Predator Helios Neo 16S (PHN16S-71-98RF).

On the Intel GPU, Windows lists every mode: 240 Hz, 165.02 Hz, 144 Hz, 120 Hz, 100.01 Hz, 75 Hz, and 60 Hz.

![Every refresh rate on the Intel GPU](pics/Screenshot%202026-10-22%20201044.png)

The laptop has an Intel GPU and an NVIDIA GeForce RTX 5070 Ti Laptop GPU. Add the rates once for each GPU.

You need Windows 11 and the .NET 10 desktop runtime. The app asks for administrator approval. Download [v1.0.0](https://github.com/hammadzaigham/PHN16S-71-98RF-Refresh-Rate-Enabler/releases/tag/v1.0.0).

## How to use

**1 — Select your GPU**

Switch to the either the Intel or NVIDIA GPU, whichever you want to set up first.

**2 — Add refresh rates**

Click **Add refresh rates**. This adds 60, 75, 100, 120, 144, and 165 Hz.

**3 — Apply the changes**

- **Intel GPU:** Click **Restart driver**. The screen may briefly turn black.
- **NVIDIA GPU:** The rates are added through the NVIDIA driver. A driver restart is normally unnecessary.

**4 — Repeat for the other GPU**

Switch to the other GPU, then click **Refresh**. Click **Add refresh rates** again. On Intel, click **Restart driver** again.

**5 — Choose a refresh rate**

Open **Windows Settings → System → Display → Advanced display**. Choose a refresh rate, and confirm if Windows asks.


## Troubleshooting

**Screen stays black?**

Press **F8 within 15 seconds** of restarting the driver. That restores 240 Hz for this session. The saved Intel rates remain, so you can restart the driver again later.

**Want to undo the changes?**

- **Intel:** Switch to the Intel GPU, click **Remove override**, then **Restart driver**.
- **NVIDIA:** Clean install the driver from the NVIDIA app.

At 60 Hz, brightness can flicker a little because of the panel's PWM dimming. Windows may show **165.02 Hz** instead of 165 Hz. That is expected.

## NVIDIA

60 Hz. The desktop mode and the active signal are both 2560×1600 at 60 Hz. Variable refresh rate is supported. Dynamic refresh rate is not available at 60 Hz.

![NVIDIA at 60 Hz](pics/Screenshot%202026-10-22%20194718.png)

120 Hz. The desktop mode and the active signal are both 120 Hz. Variable refresh rate covers 48–120 Hz, and Dynamic refresh rate can be turned on.

![NVIDIA at 120 Hz](pics/Screenshot%202026-10-22%20194724.png)

165 Hz. The desktop mode and the active signal are both 165 Hz. Variable refresh rate covers 48–165 Hz, and Dynamic refresh rate can be turned on.

![NVIDIA at 165 Hz](pics/Screenshot%202026-10-22%20194741.png)

## Intel

60 Hz. The desktop mode and the active signal are both 2560×1600 at 60 Hz. Variable refresh rate is supported by the driver.

![Intel at 60 Hz](pics/Screenshot%202026-10-22%20194823.png)

120 Hz. The desktop mode and the active signal are both 120 Hz.

![Intel at 120 Hz](pics/Screenshot%202026-10-22%20194834.png)

165.02 Hz. Windows shows 165.02 Hz. The desktop mode and the active signal match.

![Intel at 165 Hz](pics/Screenshot%202026-10-22%20194843.png)
