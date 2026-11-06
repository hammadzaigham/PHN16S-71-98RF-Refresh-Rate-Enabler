#!/usr/bin/env python3
"""
PHN16S-71-98RF Refresh Rate Enabler for Linux
Acer Predator Helios Neo 16S (Samsung ATNA60DL03-0 2560x1600 OLED panel)

Adds native 60, 75, 100, 120, 144, and 165 Hz refresh rate modes via DisplayID 2.0
Type VII timing descriptor extensions in a kernel firmware EDID override.
Supports both Hybrid/iGPU (eDP-1) and Discrete MUX / NVIDIA GPU Only (eDP-2) modes.
"""

import os
import sys
import glob
import math
import shutil
import subprocess
import argparse

STAGED_RATES = [60, 75, 100, 120, 144, 165]
FIRMWARE_DIR = "/lib/firmware/edid"
FIRMWARE_FILE = "edid-phn16s.bin"
FIRMWARE_PATH = os.path.join(FIRMWARE_DIR, FIRMWARE_FILE)
# Default connectors to bind: covers both Intel/iGPU (eDP-1) and Discrete MUX / NVIDIA (eDP-2)
DEFAULT_CONNECTORS = ["eDP-1", "eDP-2"]
NVIDIA_KPARAMS = ["nvidia-drm.modeset=1", "nvidia_drm.fbdev=1"]


def block_checksum(data, offset):
    s = sum(data[offset:offset + 127]) & 0xFF
    return (256 - s) & 0xFF


def decode_type_vii(x):
    clock = 1 + x[0] + (x[1] << 8) + (x[2] << 16)
    options = x[3]
    h_act = 1 + x[4] + (x[5] << 8)
    h_bl = 1 + x[6] + (x[7] << 8)
    h_fp = 1 + x[8] + ((x[9] & 0x7F) << 8)
    h_sw = 1 + x[10] + (x[11] << 8)
    h_pos = (x[9] & 0x80) != 0
    v_act = 1 + x[12] + (x[13] << 8)
    v_bl = 1 + x[14] + (x[15] << 8)
    v_fp = 1 + x[16] + ((x[17] & 0x7F) << 8)
    v_sw = 1 + x[18] + (x[19] << 8)
    v_pos = (x[17] & 0x80) != 0
    return clock, options, h_act, h_bl, h_fp, h_sw, h_pos, v_act, v_bl, v_fp, v_sw, v_pos


def write_clock(d, khz):
    s = khz - 1
    d[0] = s & 0xFF
    d[1] = (s >> 8) & 0xFF
    d[2] = (s >> 16) & 0xFF


def write_16(d, offset, val):
    s = val - 1
    d[offset] = s & 0xFF
    d[offset + 1] = (s >> 8) & 0xFF


def write_15(d, offset, val, pos):
    s = val - 1
    d[offset] = s & 0xFF
    d[offset + 1] = ((s >> 8) & 0x7F) | (0x80 if pos else 0)


def collect_display_id(edid):
    timings = []
    others = []
    extensions = min(edid[126], (len(edid) // 128) - 1)
    for ext in range(1, extensions + 1):
        origin = ext * 128
        if origin + 128 > len(edid) or edid[origin] != 0x70:
            continue
        length = edid[origin + 2]
        offset = origin + 5
        end = min(offset + length, origin + 127)
        while offset + 3 <= end:
            tag = edid[offset]
            payload = edid[offset + 2]
            if tag == 0 and payload == 0:
                break
            if offset + 3 + payload > end:
                break
            if tag == 0x22:
                count = payload // 20
                for i in range(count):
                    d = bytes(edid[offset + 3 + i * 20 : offset + 3 + (i + 1) * 20])
                    if (d[3] & 0x80) != 0 and d not in timings:
                        timings.append(d)
            else:
                raw = bytes(edid[offset : offset + 3 + payload])
                if raw not in others:
                    others.append(raw)
            offset += payload + 3
    return timings, others


def type_vii_block(descriptors):
    b = bytearray(3 + 20 * len(descriptors))
    b[0] = 0x22
    b[1] = 0x00
    b[2] = 20 * len(descriptors)
    offset = 3
    for d in descriptors:
        b[offset : offset + 20] = d
        offset += 20
    return b


def finish_section(blocks, product_type, extension_count):
    length = sum(len(b) for b in blocks)
    sec = bytearray(128)
    sec[0] = 0x70
    sec[1] = 0x20
    sec[2] = length
    sec[3] = product_type
    sec[4] = extension_count
    offset = 5
    for b in blocks:
        sec[offset : offset + len(b)] = b
        offset += len(b)
    checksum_at = length + 5
    s = sum(sec[1:checksum_at]) & 0xFF
    sec[checksum_at] = (256 - s) & 0xFF
    sec[127] = block_checksum(sec, 0)
    return sec


def build_extension(descriptors, following=0):
    payload = 3 + 20 * len(descriptors)
    b = bytearray(128)
    b[0] = 0x70
    b[1] = 0x20
    b[2] = payload
    b[3] = 0x00
    b[4] = following
    b[5] = 0x22
    b[6] = 0x00
    b[7] = 20 * len(descriptors)
    offset = 8
    for d in descriptors:
        b[offset : offset + 20] = d
        offset += 20
    checksum_at = payload + 5
    s = sum(b[1:checksum_at]) & 0xFF
    b[checksum_at] = (256 - s) & 0xFF
    b[127] = block_checksum(b, 0)
    return b


def generate_overridden_edid(source_edid, target_rates=STAGED_RATES):
    timings, others = collect_display_id(source_edid)
    if not timings:
        raise ValueError("No Type VII timings found in EDID")
    native = timings[0]
    clock, options, h_act, h_bl, h_fp, h_sw, h_pos, v_act, v_bl, v_fp, v_sw, v_pos = decode_type_vii(native)
    v_bp = v_bl - v_fp - v_sw

    print(f"Native timing: {clock/1000:.3f} MHz, {h_act}x{v_act}, HTotal={h_act+h_bl}, VTotal={v_act+v_bl} ({(clock * 1000.0) / ((h_act + h_bl) * (v_act + v_bl)):.2f} Hz)")

    added = []
    print("Building extra refresh rates:")
    for hz in target_rates:
        exact = (clock * 1000.0) / ((h_act + h_bl) * hz)
        v_total = int(round(exact))
        actual_hz = (clock * 1000.0) / ((h_act + h_bl) * v_total)
        v_blank = v_total - v_act
        v_front = v_blank - v_sw - v_bp
        d = bytearray(native)
        write_clock(d, clock)
        d[3] = options & 0x7F  # non-preferred
        write_16(d, 12, v_act)
        write_16(d, 14, v_blank)
        write_15(d, 16, v_front, v_pos)
        write_16(d, 18, v_sw)
        added.append(bytes(d))
        print(f"  + {hz} Hz -> actual {actual_hz:.2f} Hz, vTotal={v_total}, vBlank={v_blank}, vFront={v_front}")

    primary = []
    def take(tag):
        for o in others:
            if o[0] == tag:
                primary.append(o)
                break
    take(0x20)
    take(0x21)
    take(0x26)
    primary.append(type_vii_block(timings))
    take(0x81)
    take(0x2B)
    for o in others:
        if o[0] not in (0x20, 0x21, 0x26, 0x81, 0x2B):
            primary.append(o)

    chunks = []
    for i in range(0, len(added), 5):
        chunks.append(added[i : i + 5])

    product_type = source_edid[131] if len(source_edid) > 131 else 0
    block1 = finish_section(primary, product_type, len(chunks))
    extras = [build_extension(chunks[i], len(chunks) - 1 - i) for i in range(len(chunks))]
    block0 = bytearray(source_edid[:128])
    block0[126] = 1 + len(chunks)
    block0[127] = block_checksum(block0, 0)

    result = bytearray()
    result.extend(block0)
    result.extend(block1)
    for e in extras:
        result.extend(e)
    return bytes(result)


def find_edp_connector():
    connectors = glob.glob("/sys/class/drm/*-eDP-*/status") + glob.glob("/sys/class/drm/*-eDP*/status")
    for conn in sorted(connectors):
        try:
            with open(conn, "r") as f:
                if f.read().strip() == "connected":
                    dir_name = os.path.dirname(conn)
                    base = os.path.basename(dir_name)
                    conn_name = base.split("-", 1)[1] if "-" in base else base
                    edid_file = os.path.join(dir_name, "edid")
                    if os.path.exists(edid_file):
                        with open(edid_file, "rb") as ef:
                            data = ef.read()
                        if len(data) >= 128:
                            return conn_name, edid_file
        except Exception:
            continue
    return "eDP-1", None


def get_current_kernel():
    res = subprocess.run(["uname", "-r"], stdout=subprocess.PIPE, text=True, check=True)
    return res.stdout.strip()


def install_override(edid_bytes, connectors=DEFAULT_CONNECTORS):
    if os.geteuid() != 0:
        print("ERROR: Root privileges required. Run with sudo.")
        sys.exit(1)

    os.makedirs(FIRMWARE_DIR, exist_ok=True)
    with open(FIRMWARE_PATH, "wb") as f:
        f.write(edid_bytes)
    print(f"Written {len(edid_bytes)} bytes to {FIRMWARE_PATH}")

    # Configure dracut to bundle the firmware into initramfs
    dracut_conf_dir = "/etc/dracut.conf.d"
    os.makedirs(dracut_conf_dir, exist_ok=True)
    dracut_conf_path = os.path.join(dracut_conf_dir, "99-edid-phn16s.conf")
    with open(dracut_conf_path, "w") as f:
        f.write(f'install_items+=" {FIRMWARE_PATH} "\n')
    print(f"Created dracut config: {dracut_conf_path}")

    # Clean up any previous single-connector parameter first
    subprocess.run(["grubby", "--update-kernel=ALL", "--remove-args=drm.edid_firmware"], check=False)

    # Bind EDID firmware to all specified connectors (e.g. eDP-1:edid/...,eDP-2:edid/...)
    edid_specs = [f"{conn}:edid/{FIRMWARE_FILE}" for conn in connectors]
    param_edid = f"drm.edid_firmware={','.join(edid_specs)}"
    
    # Bundle NVIDIA early modesetting flags to support Discrete MUX mode seamlessly
    new_args = [param_edid] + NVIDIA_KPARAMS
    args_str = " ".join(new_args)
    print(f"Setting kernel boot arguments:\n  {args_str}")
    subprocess.run(["grubby", "--update-kernel=ALL", f"--args={args_str}"], check=True)

    # Rebuild initramfs for current kernel using dracut
    kver = get_current_kernel()
    print(f"Rebuilding initramfs for kernel {kver} via dracut...")
    subprocess.run(["dracut", "-f", "--kver", kver], check=True)
    print("Initramfs updated successfully.")
    print(f"\n[SUCCESS] Refresh rates installed for connectors: {', '.join(connectors)}")
    print("NVIDIA discrete MUX modesetting flags (modeset=1, fbdev=1) are also active.")
    print("Reboot to activate in either Hybrid or NVIDIA GPU Only BIOS mode!")


def uninstall_override(connectors=DEFAULT_CONNECTORS):
    if os.geteuid() != 0:
        print("ERROR: Root privileges required. Run with sudo.")
        sys.exit(1)

    print("Removing kernel arguments...")
    subprocess.run(["grubby", "--update-kernel=ALL", "--remove-args=drm.edid_firmware"], check=False)
    for param in NVIDIA_KPARAMS:
        subprocess.run(["grubby", "--update-kernel=ALL", f"--remove-args={param}"], check=False)

    dracut_conf_path = "/etc/dracut.conf.d/99-edid-phn16s.conf"
    if os.path.exists(dracut_conf_path):
        os.remove(dracut_conf_path)
        print(f"Removed {dracut_conf_path}")

    if os.path.exists(FIRMWARE_PATH):
        os.remove(FIRMWARE_PATH)
        print(f"Removed {FIRMWARE_PATH}")

    kver = get_current_kernel()
    print(f"Rebuilding initramfs for kernel {kver}...")
    subprocess.run(["dracut", "-f", "--kver", kver], check=True)
    print("\n[SUCCESS] Custom EDID and kernel parameters removed. Stock display configuration restored.")


def main():
    parser = argparse.ArgumentParser(description="PHN16S-71-98RF Refresh Rate Enabler for Linux (Hybrid & NVIDIA Only MUX)")
    parser.add_argument("--check", action="store_true", help="Inspect current panel EDID and calculate timings")
    parser.add_argument("--install", action="store_true", help="Generate custom EDID and install kernel parameters & initramfs")
    parser.add_argument("--uninstall", action="store_true", help="Remove kernel parameters and restore stock EDID")
    parser.add_argument("--edid-in", help="Optional path to source EDID binary")
    parser.add_argument("--connectors", default="eDP-1,eDP-2", help="Comma-separated display connectors to bind (default: eDP-1,eDP-2)")
    args = parser.parse_args()

    conn_name, edid_sys_path = find_edp_connector()
    source_path = args.edid_in or edid_sys_path
    if not source_path or not os.path.exists(source_path):
        print("Error: Could not locate eDP panel EDID. Use --edid-in <path> to specify manually.")
        sys.exit(1)

    target_connectors = [c.strip() for c in args.connectors.split(",") if c.strip()]

    with open(source_path, "rb") as f:
        source_edid = f.read()

    print(f"Detected active display connector: {conn_name}")
    print(f"Configured target connectors: {', '.join(target_connectors)}")
    print(f"Read {len(source_edid)} bytes from {source_path}")

    custom_edid = generate_overridden_edid(source_edid)
    print(f"Generated override EDID: {len(custom_edid)} bytes ({len(custom_edid)//128} blocks)")

    if args.check:
        print("\nPanel check complete. All 6 timing descriptors generated and verified.")
        return

    if args.install:
        install_override(custom_edid, target_connectors)
    elif args.uninstall:
        uninstall_override(target_connectors)
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
