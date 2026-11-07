#!/bin/sh
# Select the override before DRM device probing. Fail closed to stock EDID.
preysense_intel_boot_display() {
    for dev in "$1"/bus/pci/devices/*; do
        [ -r "$dev/boot_vga" ] || continue
        read -r primary < "$dev/boot_vga"
        [ "$primary" = 1 ] || continue
        read -r vendor < "$dev/vendor" || return 1
        [ "$vendor" = 0x8086 ] || return 1
        return 0
    done
    return 1
}

preysense_select_edid() {
    case " $(cat /proc/cmdline) " in
        *' rd.preysense.edid=0 '*) return 0 ;;
    esac
    preysense_intel_boot_display /sys || return 0
    [ -r /lib/firmware/edid/edid-phn16s.bin ] || return 0
    # Intel owns eDP-1 in Hybrid; NVIDIA's disconnected eDP-2 is untouched.
    if [ -w /sys/module/drm/parameters/edid_firmware ]; then
        printf '%s' 'eDP-1:edid/edid-phn16s.bin' > /sys/module/drm/parameters/edid_firmware
    else
        modprobe drm edid_firmware=eDP-1:edid/edid-phn16s.bin
    fi
}
preysense_select_edid
