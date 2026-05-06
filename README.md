# BrightnessTrayAppWPF

A Windows 11 tray application for actual external monitor brightness control using DDC/CI, with some extras.

## Features
* Highly responsive DDC/CI control, with a robust recovery and verification system.
* Robust Windows Night light control
* Scrollwheel interactions with tray icon, extended mouse and modifier quick actions
* Hotkeys
* Automatic environmental controls - sunrise / sunset interactive curve editor
* Master control slider, slider offsets, synchronization, etc.
* Hot-swappable profiles
* Flyout customization (visibility of features, docking, slider tracking, user inputs)
* Themeability (live color pickers, light/dark mode, glyph customization, look and feel, etc.)
* Single packaged portable exe with fully self-contained install and update system.
* and more.



# TODO
* finish keyboard accessibility for settings menu

* more consistent / complete OS theme following

* forward compat / versioning system for settings / profiles xml files.
	* version number should work like the build number system, just an int
	* every increment is required to have a forward compat section filled out
* Add HDMI relink project as optional toggle setting
* feature: logarithmic scale for monitor offsets master slider drag: when moving monitors by the master slider, they should increment by the logarithmic increment normalized to 0 to the maximum current any-individual slider value.

* feature: weather api hooking and offset curve in environmental panel

* feature: ALS (ambient light sensor) support in environmental panel

### Future
* nightlight - it is absolutely *possible* for it to work per-monitor. but it'd diverge from the built in night light, and would be a nightmare to get working right.
* gamma lut manipulation - put in "ultra dim" mode that works the same way tools like f.lux do. this should only work on the master slider since screwing with luts is a global thing. so master slider should have the power to go negative in value or something similar.



## Thanks and credit to:
https://github.com/udivankin/sunrise-sunset for the SPA implementation I ported to C#
https://github.com/xanderfrangos/twinkle-tray for the flyout UI inspiration
