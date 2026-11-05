# Nib on the Lenovo Yoga 7 2-in-1 14" Gen 10 (14ILL10) with a Core Ultra 7 256V

Nib detects this machine and tunes itself for it automatically. Nothing needs configuring. This page
explains what changes and why, what to set in Windows, how to build a CPU-tuned copy, and how to check
that the tuning works.

> These optimizations have been unit-tested and type-checked, but **not yet run on a real Yoga 7**.
> The checks in §5 will show whether each one works on your unit.

## 1. Your hardware, and what Nib does with it

| Part | What it is | What Nib does |
|---|---|---|
| CPU | Core Ultra 7 256V (Lunar Lake): 4 Lion Cove performance cores + 4 Skymont low-power efficiency cores, 8 threads, 16 GB LPDDR5X memory on the chip package | While the pen is within hover range, the ink thread is restricted to the 4 performance cores (Windows CPU sets, which, unlike hard affinity, stay compatible with Windows power management). This is released 1.5 s after the pen leaves. The ink thread also runs at highest priority, under the MMCSS "Games" class, and opted out of EcoQoS, so battery saver doesn't slow it. |
| GPU | Intel Arc 140V (Xe2) | D3D11 device with frame latency 1. The page swap chain is now **opaque**, so Windows can skip blending it and, where the GPU supports it, scan it out from a hardware overlay plane. |
| Display | 14" 16:10 OLED touch screen: **WUXGA 1920×1200 at 60 Hz** or **2.8K 2880×1800 at 120 Hz**, depending on the configuration | **Automatic stroke prediction** from the live refresh rate: 15 ms at 60 Hz, 9 ms at 120 Hz. It is re-read at every pen-down, so it follows Dynamic refresh rate. **High refresh on hover:** when the pen comes near, Nib asks Windows for the panel's high refresh rate (`DCompositionBoostCompositorClock`), so the first stroke is already drawn at 120 Hz. **Pure black dark pages:** OLED pixels are switched off on dark pages. |
| Pen | Lenovo Yoga Pen or Lenovo Digital Pen: Microsoft Pen Protocol 2.0, 4096 pressure levels (the Yoga Pen also reports tilt), two side buttons, no tail eraser | Nib reads the pen's **HID descriptor** at startup, so it knows the exact pressure resolution, whether tilt is reported, and which buttons exist, without guessing from samples. If the pen has an eraser button, the barrel button defaults to **lasso** (eraser on one button, lasso on the other). Windows' own pen ripples and press-and-hold ring are turned off for Nib's window; Nib shows its own nib-shaped hover cursor instead. |
| Form factor | 360° convertible | **Tablet posture** (keyboard folded back) switches the toolbar to 48-pixel touch-sized buttons. Laptop posture returns it to 40 pixels. The touch keyboard and rotation were already handled. |

You can see what Nib detected in **Settings → This device**. The debug overlay (Ctrl+Alt+Shift+F12)
shows the prediction in use and the live performance-mode state.

The defaults chosen from the device (barrel button = lasso, pure black dark pages) apply only the first
time Nib runs on a machine. After that, your own settings always win.

## 2. Latency you can expect

Software pipeline estimate (the same model as the perf suite). This doesn't include the digitizer's
own scan delay, which only a camera measurement captures:

$$t_{\text{pen→pixel}} \approx t_{\text{processing}} + 1.5\,T_{\text{refresh}} - t_{\text{prediction}}$$

| Panel | $1.5\,T_{\text{refresh}}$ | Prediction | Estimate |
|---|---|---|---|
| WUXGA OLED, 60 Hz | 25 ms | 15 ms | $4 + 25 - 15 = 14$ ms |
| 2.8K OLED, 120 Hz | 12.5 ms | 9 ms | $4 + 12.5 - 9 = 7.5$ ms |

If you have the **60 Hz panel**, prediction carries most of the weight. Leave **Settings → Stroke
prediction** on *Automatic*. On the **120 Hz panel**, Nib meets the 20 ms target even with prediction
off.

## 3. Windows settings worth checking

1. **Refresh rate** (Settings → System → Display → Advanced display). On the 2.8K panel, *Dynamic* is
   fine, because Nib requests 120 Hz while you write. For the absolute lowest latency when plugged in,
   pick a fixed 120 Hz.
2. **Power mode** (Settings → System → Power & battery). *Best performance* when plugged in gives the
   most headroom. On battery, Nib stays smooth in *Best power efficiency* too, because it opts its ink
   thread out of power throttling.
3. **Pen buttons.** Leave the side buttons on their Windows defaults (right-click / eraser). If a Lenovo
   pen utility remaps them to keyboard shortcuts, Nib no longer sees them as pen buttons.
4. **Ink offset.** If ink appears a little beside the nib at the screen edges, calibrate the pen in
   Control Panel → *Tablet PC Settings* → *Calibrate*.

## 4. Build a copy tuned for the 256V

Native AOT normally targets any x64 CPU. On Lunar Lake you can let it use AVX2, FMA and BMI2:

```powershell
dotnet publish src\Nib.App -c Release -r win-x64 -p:Platform=x64 -p:NibCpu=lunarlake -o artifacts\app-yoga7
# or, when building on the Yoga itself: -p:NibCpu=native
```

The gain is a few percent in geometry and PDF export, and nothing gets slower. The resulting `Nib.exe`
refuses to start on CPUs without those instructions, so share the default build instead.

Run the perf gate with the tuned build, once plugged in and once on battery saver:

```powershell
.\perf\run-perf.ps1 -Cpu lunarlake -Label "Yoga 7 14ILL10, plugged in"
.\perf\run-perf.ps1 -Cpu lunarlake -Label "Yoga 7 14ILL10, battery saver"
```

## 5. Checks on the device

- [ ] **Settings → This device** names the Yoga 7 2-in-1 14ILL10 and shows the right panel (resolution,
  refresh rate, OLED), the pen with 4096 pressure levels (and tilt for the Yoga Pen), and the CPU as
  4 performance + 4 efficiency logical processors.
- [ ] Debug overlay: hovering the pen shows *performance mode on, high refresh requested, ink thread on
  4 P-cores*. About 1.5 s after the pen leaves, it shows *performance mode idle*.
- [ ] With *Dynamic* refresh rate selected: the overlay's refresh rate reads 120 Hz from the first stroke
  after the pen arrives (not 60 Hz for the first stroke and 120 Hz later).
- [ ] Resource Monitor → CPU while writing quickly: Nib's load sits on four of the eight CPU graphs
  (the performance cores) instead of moving across all eight.
- [ ] Prediction: on the 120 Hz panel, the overlay shows *prediction 9 ms*. Switch Windows to 60 Hz and
  it shows *15 ms* from the next stroke.
- [ ] Pen buttons: holding the eraser button erases. The barrel button does lasso. Neither shows the
  Windows ripple or press-and-hold ring on the page.
- [ ] Fold into tablet posture: the toolbar grows to touch size within a second. Fold back: it shrinks.
- [ ] Dark theme: pages are pure black, ink is light, and colored ink keeps its hue. Exports keep the
  real colors.
- [ ] On battery saver, write for 10 minutes. The input→present p95 stays within target, and the
  battery drain is close to the drain with **Performance while writing** off (Settings → Pen & touch).
  If it isn't, turn that setting off and report what you see.
- [ ] Camera measurement (`TEST-CHECKLIST.md` §2) on your panel, compared with OneNote on the same
  device.

## Sources

- [Notebookcheck review of the Yoga 7 2-in-1 14 G10](https://www.notebookcheck.net/1-000-nit-OLED-at-120-Hz-inside-a-convertible-Lenovo-Yoga-7-14-2in1-G10-review.991565.0.html)
  (2.8K OLED 120 Hz panel, Lunar Lake core layout)
- [14ILL10 specification sheet (83JQ0039YA)](https://datika.me/wa-data/public/site/data/datika.me/Laptopovi/Yoga_7_2_in_1_14ILL10_83JQ0039YA.pdf)
  (4P + 4LPE, panel options)
- [Core Ultra 7 256V with WUXGA OLED (retail listing)](https://app.virginmegastore.ae/electronics-accessories/computers-accessories/computers/laptop-computers/lenovo-yoga-7-2-in-1-laptop-intel-core-ultra-7-256v-16gb-ram-512gb-ssd-integrated-intel-arc-graphics-140v-14-inch-wuxga-1920x1200-oled-touch-windows-11-home---luna-grey/p/1121084)
- [Lenovo Yoga Pen GX81S07448](https://www.ballicom.co.uk/lenovo-gx81s07448-yoga-pen-p-stylus-pen-13-5-g-.p1723475.html)
  (MPP 2.0, 4096 levels, tilt, lists Yoga 7 2-in-1 14ILL10)
- [Lenovo Digital Pen 2](https://www.bhphotovideo.com/c/product/1917541-REG/lenovo_4x81h95633_digital_pen_2_gray.html)
  (4096 levels, two side buttons, no eraser end)
- [DCompositionBoostCompositorClock](https://learn.microsoft.com/en-us/windows/win32/api/dcomp/nf-dcomp-dcompositionboostcompositorclock),
  [Dynamic refresh rate](https://devblogs.microsoft.com/directx/dynamic-refresh-rate/)
- [SetThreadSelectedCpuSets](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadselectedcpusets),
  [FEEDBACK_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ne-winuser-feedback_type)
- [Native AOT optimizing: IlcInstructionSet](https://github.com/dotnet/runtime/blob/main/src/coreclr/nativeaot/docs/optimizing.md)
